// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Search;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Search;

/// <summary>
/// arctic-m's tokenizer, which bounds every chunk at 512 tokens whichever provider embeds it (structural chunking
/// §3.2). Kept where the arctic-m download keeps it, so one verified file serves both; downloaded and checked by its
/// SHA-256 when an embedding provider is active and it isn't there. A file already in place is used even without a
/// provider. A failed download is tried again twice, after a pause; then the character bound
/// (<see cref="ChunkBound.Fallback"/>) applies until the next start.
/// </summary>
public sealed class ChunkTokenizer(IOptions<KumbuOptions> options, IHttpClientFactory http, ILogger<ChunkTokenizer> log) : BackgroundService
{
    public const string HttpClientName = nameof(ChunkTokenizer);

    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile ChunkBound _bound = ChunkBound.Fallback;

    /// <summary>How long a download may take in all; settable for tests.</summary>
    internal TimeSpan DownloadLimit { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a download may wait for its next bytes (<see cref="HttpClient.Timeout"/> ends with the
    /// response's headers); settable for tests.</summary>
    internal TimeSpan IdleLimit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The pauses before the second and third attempt; settable for tests.</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>Until <see cref="Settled"/>, the fallback.</summary>
    public ChunkBound Bound => _bound;

    /// <summary>Loaded, or clear that there is none.</summary>
    public bool Settled => _settled.Task.IsCompleted;

    public Task WhenSettledAsync(CancellationToken ct) => _settled.Task.WaitAsync(ct);

    /// <summary>Whether anything is embedded on this server: by the internal model or by an external provider.</summary>
    public static bool Needed(KumbuOptions o) => o.Search.Enabled || o.Embeddings.Providers.Count > 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var file = ModelFiles.ChunkTokenizer;
        var path = Path.Combine(ModelFiles.Directory(o.Search.ModelPath, ModelFiles.Default), ModelFiles.Tokenizer);
        try
        {
            if (!await ModelFiles.MatchesAsync(path, file, stoppingToken))
            {
                if (!Needed(o))
                {
                    _bound = ChunkBound.Unbounded;
                    return;
                }

                await DownloadWithRetriesAsync(file, path, stoppingToken);
            }

            await using (var stream = File.OpenRead(path)) _bound = ChunkBound.Of(UnigramTokenizer.Load(stream), file.Sha256);
            log.LogInformation("Chunks are bounded by the tokenizer in {Path}", path);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            _bound = Needed(o) ? ChunkBound.Fallback : ChunkBound.Unbounded;
            log.LogWarning("The tokenizer isn't available ({Message}); search chunks by a character bound until the next start", e.Message);
        }
        finally
        {
            _settled.TrySetResult();
        }
    }

    /// <summary>A failure that settled on the fallback would chunk every note anew, and send it to an external provider
    /// again, at each start that the download fails or succeeds; so a passing failure is waited out first.</summary>
    private async Task DownloadWithRetriesAsync(ModelFile file, string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadAsync(file, path, ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && attempt < RetryDelays.Count)
            {
                log.LogWarning("Downloading the tokenizer failed ({Message}); trying again in {Delay}", e.Message, RetryDelays[attempt]);
            }

            await Task.Delay(RetryDelays[attempt], ct);
        }
    }

    /// <summary>As <see cref="OnnxEmbedder"/> downloads, but through a part file of its own, so the two never write one
    /// file; each moves only a verified file into place.</summary>
    private async Task DownloadAsync(ModelFile file, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        log.LogInformation("Downloading the tokenizer ({Megabytes} MB) into {Path}", file.Size / 1_000_000, path);
        var part = path + ".chunking.part";
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(DownloadLimit);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        idle.CancelAfter(IdleLimit);
        try
        {
            using (var client = http.CreateClient(HttpClientName))
            using (var response = await client.GetAsync(ModelFiles.Get(ModelFiles.Default).Url(file), HttpCompletionOption.ResponseHeadersRead, idle.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
                await using var target = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                // A server that stalls is given up on, not waited for until the next start.
                while ((read = await source.ReadAsync(buffer, idle.Token)) > 0)
                {
                    idle.CancelAfter(IdleLimit);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    // A server that sends more than the file holds is cut off, not written to the disk without end.
                    if (done > file.Size) throw new InvalidDataException($"{file.Local} is longer than it should be");
                }

                if (done != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    throw new InvalidDataException($"{file.Local} doesn't match its checksum");
            }

            File.Move(part, path, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(total.IsCancellationRequested ? "the download took too long" : "the download stalled");
        }
        finally
        {
            File.Delete(part);
        }
    }
}
