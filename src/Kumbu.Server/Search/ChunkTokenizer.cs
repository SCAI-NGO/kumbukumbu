// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Search;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Search;

/// <summary>
/// arctic-m's tokenizer, which bounds every chunk at 512 tokens whichever provider embeds it (structural chunking
/// §3.2). Kept where the arctic-m download keeps it, so one verified file serves both; downloaded and checked by its
/// SHA-256 when an embedding provider is active and it isn't there. A file already in place is used even without a
/// provider. No retries: a failed download leaves the character bound (<see cref="ChunkBound.Fallback"/>) until the next start.
/// </summary>
public sealed class ChunkTokenizer(IOptions<KumbuOptions> options, IHttpClientFactory http, ILogger<ChunkTokenizer> log) : BackgroundService
{
    public const string HttpClientName = nameof(ChunkTokenizer);

    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Until <see cref="Settled"/>, the fallback.</summary>
    public ChunkBound Bound { get; private set; } = ChunkBound.Fallback;

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
                    Bound = ChunkBound.Unbounded;
                    return;
                }

                await DownloadAsync(file, path, stoppingToken);
            }

            await using (var stream = File.OpenRead(path)) Bound = ChunkBound.Of(UnigramTokenizer.Load(stream), file.Sha256);
            log.LogInformation("Chunks are bounded by the tokenizer in {Path}", path);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            Bound = Needed(o) ? ChunkBound.Fallback : ChunkBound.Unbounded;
            log.LogWarning("The tokenizer isn't available ({Message}); search chunks by a character bound until the next start", e.Message);
        }
        finally
        {
            _settled.TrySetResult();
        }
    }

    /// <summary>As <see cref="OnnxEmbedder"/> downloads, but through a part file of its own, so the two never write one
    /// file; each moves only a verified file into place.</summary>
    private async Task DownloadAsync(ModelFile file, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        log.LogInformation("Downloading the tokenizer ({Megabytes} MB) into {Path}", file.Size / 1_000_000, path);
        var part = path + ".chunking.part";
        try
        {
            using (var client = http.CreateClient(HttpClientName))
            using (var response = await client.GetAsync(ModelFiles.Get(ModelFiles.Default).Url(file), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                }

                if (done != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    throw new InvalidDataException($"{file.Local} doesn't match its checksum");
            }

            File.Move(part, path, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }
    }
}
