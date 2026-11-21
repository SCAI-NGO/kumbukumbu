// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Search;

/// <summary>
/// The HTTP clients of the embedding providers (<see cref="EmbeddingDrivers.HttpClientName"/>), whichever their name: they
/// follow no redirect, so a provider's answer, or whatever answers in its place, can't send the vault's text and the key on
/// to another address. A 3xx then fails the request as any status that isn't transient does.
/// </summary>
public sealed class EmbeddingClients : IConfigureNamedOptions<HttpClientFactoryOptions>
{
    public void Configure(string? name, HttpClientFactoryOptions options)
    {
        if (name is null || !name.StartsWith(EmbeddingDrivers.HttpClientName(""), StringComparison.Ordinal)) return;
        options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    public void Configure(HttpClientFactoryOptions options) => Configure(Options.DefaultName, options);
}
