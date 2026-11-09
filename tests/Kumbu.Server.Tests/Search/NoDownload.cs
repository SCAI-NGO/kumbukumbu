// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Search;

/// <summary>Test hosts never download the tokenizer: its client refuses every request, so a host with a provider
/// configured and no file in place gets the fallback bound.</summary>
internal sealed class NoDownload : HttpMessageHandler
{
    public static void Register(IServiceCollection services) =>
        services.AddHttpClient(ChunkTokenizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new NoDownload());

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException($"Test hosts don't download ({request.RequestUri}).");
}
