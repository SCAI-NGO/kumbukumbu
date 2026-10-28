// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Search;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kumbu.Server.Search;

/// <summary>
/// The internal embedding model (the <c>onnx</c> driver), in process via ONNX Runtime: plaintext never leaves the server (concept §6). On the
/// first start it downloads the model into <c>Kumbu:Search:ModelPath</c> and checks each file's SHA-256, as it does again
/// at every later start; after that it runs offline. Until the model is loaded, search ranks by words only and says why.
/// </summary>
public sealed class OnnxEmbedder(IOptions<KumbuOptions> options, IHttpClientFactory http, ILogger<OnnxEmbedder> log)
    : BackgroundService, IEmbeddingDriver
{
    private const int BatchSize = 8;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EmbeddingModel _model = ModelFiles.Get(ModelFiles.Default);
    private InferenceSession? _session;
    private UnigramTokenizer? _tokenizer;

    public SemanticState State { get; private set; } = SemanticState.Preparing;

    public string ModelKey => options.Value.Search.Model;

    public int Dimensions => ModelFiles.Get(ModelKey).Dimensions;

    /// <summary>Any number would do (it runs batches of eight); this keeps one call's work, and its wait, short.</summary>
    public int MaxBatch => 64;

    public string? Reason { get; private set; } = "the embedding model is loading";

    public Task WhenReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Search;
        _model = ModelFiles.Get(o.Model);
        if (!o.Enabled)
        {
            (State, Reason) = (SemanticState.Off, "search by meaning is switched off on this server (Kumbu:Search:Enabled)");
            return;
        }

        try
        {
            ModelFiles.AdoptStep7Layout(o.ModelPath);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            log.LogWarning("The model files of step 7 in {Path} couldn't be moved into {Key}/ ({Message}); the model is downloaded there instead", o.ModelPath, ModelFiles.Default, e.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dir = ModelFiles.Directory(o.ModelPath, o.Model);
                await DownloadAsync(_model, dir, stoppingToken);
                Load(dir, o.Threads);
                (State, Reason) = (SemanticState.Ready, null);
                _ready.TrySetResult();
                log.LogInformation("Embedding model loaded ({Key}: {Repository} at {Revision})", _model.Key, _model.Repository, _model.Revision[..8]);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OnnxRuntimeException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                (State, Reason) = (SemanticState.Off, $"the embedding model isn't available ({e.Message}); the server tries again every {RetryAfter.TotalMinutes} minutes");
                log.LogError("Embedding model not available: {Message}", e.Message);
                await Task.Delay(RetryAfter, stoppingToken);
            }
        }
    }

    private async Task DownloadAsync(EmbeddingModel model, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        foreach (var file in model.Files)
        {
            var (_, local, size, sha) = file;
            var path = Path.Combine(dir, local);
            if (ModelFiles.HasSize(path, size))
            {
                // At every start, not only at download: a file of the same size put on the volume would otherwise be
                // loaded. In the background like the download, before the model is loaded, so search ranks by words
                // until then; a file that doesn't match is never loaded, but downloaded again.
                if (await ModelFiles.MatchesAsync(path, file, ct)) continue;
                log.LogWarning("{File} of the embedding model in {Dir} doesn't match its checksum; it is downloaded again", local, dir);
                File.Delete(path);
            }

            (State, Reason) = (SemanticState.Preparing, $"the embedding model is being downloaded ({size / 1_000_000} MB, once)");
            log.LogInformation("Downloading {File} of the embedding model ({Megabytes} MB) into {Dir}", local, size / 1_000_000, dir);
            var part = path + ".part";
            using (var client = http.CreateClient(nameof(OnnxEmbedder)))
            using (var response = await client.GetAsync(model.Url(file), HttpCompletionOption.ResponseHeadersRead, ct))
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
        var session = new InferenceSession(Path.Combine(dir, ModelFiles.Model), sessionOptions);
        if (!session.OutputMetadata.TryGetValue("sentence_embedding", out var output) || output.Dimensions.Length != 2 || output.Dimensions[1] != _model.Dimensions)
        {
            session.Dispose();
            throw new InvalidDataException($"{_model.Key} has no sentence_embedding output of {_model.Dimensions} values");
        }

        _session = session;
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (_session is not { } session || _tokenizer is not { } tokenizer) throw new InvalidOperationException("The embedding model isn't loaded");
        var tokens = texts.Select(t => tokenizer.Encode(query ? _model.QueryPrompt + t : t)).ToList();
        var result = new float[texts.Count][];
        // Similar lengths together, so a batch pads little.
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => tokens[i].Count).ToList();
        foreach (var batch in order.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var vectors = await Task.Run(() => Run(session, _model.Dimensions, [.. batch.Select(i => tokens[i])]), ct);
            for (var k = 0; k < batch.Length; k++) result[batch[k]] = vectors[k];
        }

        return result;
    }

    private static float[][] Run(InferenceSession session, int dimensions, List<List<int>> batch)
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
            var v = new float[dimensions];
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
