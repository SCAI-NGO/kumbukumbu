// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Net;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Tests.Net;

/// <summary>The address checks shared by every client that fetches from an address a stranger may choose. No test reaches
/// the network: the only request goes to localhost and is refused before a connection is made.</summary>
public sealed class PublicAddressesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Services() =>
        new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new KumbuOptions())).AddClientMetadataFetcher().BuildServiceProvider();

    [Theory]
    [MemberData(nameof(ClientMetadataTests.Addresses), MemberType = typeof(ClientMetadataTests))]
    public void Same_answers_as_the_oauth_fetch(string address, bool expected)
    {
        Assert.Equal(expected, PublicAddresses.IsPublicAddress(IPAddress.Parse(address)));
        Assert.Equal(expected, ClientMetadataFetcher.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task The_oauth_client_still_refuses_loopback()
    {
        using var services = Services();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientMetadataServices.HttpClientName);
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://localhost:1/", Ct));
        Assert.IsType<NotPublicAddressException>(failed.InnerException);
    }

    [Fact]
    public void The_oauth_handler_follows_no_redirects_and_takes_no_cookies_or_proxy()
    {
        using var services = Services();
        HttpMessageHandler handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientMetadataServices.HttpClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseProxy);
        Assert.False(sockets.UseCookies);
        Assert.NotNull(sockets.ConnectCallback);
    }
}
