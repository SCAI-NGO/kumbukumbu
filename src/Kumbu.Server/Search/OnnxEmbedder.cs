// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Search;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kumbu.Server.Search;

/// <summary>
/// The embedding model, in process via ONNX Runtime: plaintext never leaves the server (concept §6). On the
/// first start it downloads the model into <c>Kumbu:Search:ModelPath</c> and checks each file's SHA-256; after
/// that it runs offline. Until the model is loaded, search ranks by words only and says why.
/// </summary>
public sealed class OnnxEmbedder(IOptions<KumbuOptions> options, IHttpClientFactory http, ILogger<OnnxEmbedder> log)
    : BackgroundService, IEmbedder
{
    private const int BatchSize = 8;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InferenceSession? _session;
    private UnigramTokenizer? _tokenizer;

    public SemanticState State { get; private set; } = SemanticState.Preparing;

    public string? Reason { get; private set; } = "the embedding model is loading";

    public Task WhenReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Search;
        if (!o.Enabled)
        {
            (State, Reason) = (SemanticState.Off, "search by meaning is switched off on this server (Kumbu:Search:Enabled)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DownloadAsync(o.ModelPath, stoppingToken);
                Load(o.ModelPath, o.Threads);
                (State, Reason) = (SemanticState.Ready, null);
                _ready.TrySetResult();
                log.LogInformation("Embedding model loaded ({Repository} at {Revision})", ModelFiles.Repository, ModelFiles.Revision[..8]);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or OnnxRuntimeException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                (State, Reason) = (SemanticState.Off, $"the embedding model isn't available ({e.Message}); the server tries again every {RetryAfter.TotalMinutes} minutes");
                log.LogError("Embedding model not available: {Message}", e.Message);
                await Task.Delay(RetryAfter, stoppingToken);
            }
        }
    }

    private async Task DownloadAsync(string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        foreach (var (remote, local, size, sha) in ModelFiles.Files)
        {
            var path = Path.Combine(dir, local);
            if (File.Exists(path) && new FileInfo(path).Length == size) continue;

            (State, Reason) = (SemanticState.Preparing, $"the embedding model is being downloaded ({size / 1_000_000} MB, once)");
            log.LogInformation("Downloading {File} of the embedding model ({Megabytes} MB) into {Dir}", local, size / 1_000_000, dir);
            var part = path + ".part";
            using (var client = http.CreateClient(nameof(OnnxEmbedder)))
            using (var response = await client.GetAsync(ModelFiles.Url(remote), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long done = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (done - reported >= size / 10)
                    {
                        reported = done;
                        log.LogInformation("{File}: {Percent}%", local, done * 100 / size);
                    }
                }

                if (done != size || Convert.ToHexStringLower(hash.GetHashAndReset()) != sha)
                {
                    target.Close();
                    File.Delete(part);
                    throw new InvalidDataException($"{local} doesn't match its checksum");
                }
            }

            File.Move(part, path, overwrite: true);
        }
    }

    private void Load(string dir, int threads)
    {
        using (var stream = File.OpenRead(Path.Combine(dir, ModelFiles.Tokenizer))) _tokenizer = UnigramTokenizer.Load(stream);
        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Leaves cores for answering requests while notes are indexed.
            IntraOpNumThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2),
        };
        _session = new InferenceSession(Path.Combine(dir, ModelFiles.Model), sessionOptions);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (_session is not { } session || _tokenizer is not { } tokenizer) throw new InvalidOperationException("The embedding model isn't loaded");
        var tokens = texts.Select(t => tokenizer.Encode(query ? ModelFiles.QueryPrompt + t : t)).ToList();
        var result = new float[texts.Count][];
        // Similar lengths together, so a batch pads little.
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => tokens[i].Count).ToList();
        foreach (var batch in order.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var vectors = await Task.Run(() => Run(session, [.. batch.Select(i => tokens[i])]), ct);
            for (var k = 0; k < batch.Length; k++) result[batch[k]] = vectors[k];
        }

        return result;
    }

    private static float[][] Run(InferenceSession session, List<List<int>> batch)
    {
        var length = batch.Max(t => t.Count);
        var ids = new DenseTensor<long>([batch.Count, length]);
        var mask = new DenseTensor<long>([batch.Count, length]);
        for (var b = 0; b < batch.Count; b++)
        {
            for (var i = 0; i < length; i++)
            {
                ids[b, i] = i < batch[b].Count ? batch[b][i] : UnigramTokenizer.PadId;
                mask[b, i] = i < batch[b].Count ? 1 : 0;
            }
        }

        using var outputs = session.Run(
            [NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask)],
            ["sentence_embedding"]);
        var embedding = outputs[0].AsTensor<float>();
        var result = new float[batch.Count][];
        for (var b = 0; b < batch.Count; b++)
        {
            var v = new float[ModelFiles.Dimensions];
            for (var d = 0; d < v.Length; d++) v[d] = embedding[b, d];
            result[b] = Vectors.Normalized(v);
        }

        return result;
    }

    public override void Dispose()
    {
        _session?.Dispose();
        base.Dispose();
    }
}

public static class Vectors
{
    public static float[] Normalized(float[] v)
    {
        var norm = MathF.Sqrt(Dot(v, v));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => System.Numerics.Tensors.TensorPrimitives.Dot(a, b);
}
