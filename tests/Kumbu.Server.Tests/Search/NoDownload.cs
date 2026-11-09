// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Tests.Search;

/// <summary>Test hosts never download the tokenizer: its client refuses every request, and the first refusal is
/// final, so a host with a provider configured and no file in place gets the fallback bound at once.</summary>
internal sealed class NoDownload : HttpMessageHandler
{
    public static void Register(IServiceCollection services)
    {
        services.AddHttpClient(ChunkTokenizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new NoDownload());
        services.AddSingleton(sp => new ChunkTokenizer(
            sp.GetRequiredService<IOptions<KumbuOptions>>(), sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<ChunkTokenizer>>()) { RetryDelays = [] });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException($"Test hosts don't download ({request.RequestUri}).");
}
