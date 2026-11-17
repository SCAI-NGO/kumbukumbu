// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Server.Api;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Api;

public sealed class UploadTicketsTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly UploadTickets _tickets;

    public UploadTicketsTests() => _tickets = new UploadTickets(_time);

    private static byte[] Bearer() => [1, 2, 3, 4];

    private string Issue(byte[] bearer, string holder = "key-1") =>
        _tickets.Issue(bearer, null, "/Work/x.png", null, holder).Token;

    [Fact]
    public void Issued_ticket_is_taken_once()
    {
        var token = Issue(Bearer());

        var ticket = _tickets.Take(token);

        Assert.NotNull(ticket);
        Assert.Equal("/Work/x.png", ticket.Path);
        Assert.Null(_tickets.Take(token));
    }

    [Fact]
    public void Expired_ticket_is_gone_and_zeroed()
    {
        var bearer = Bearer();
        var token = Issue(bearer);

        _time.Advance(UploadTickets.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Null(_tickets.Take(token));
        Assert.All(bearer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Twenty_first_ticket_drops_the_oldest()
    {
        var first = Bearer();
        var firstToken = Issue(first);
        var other = Issue(Bearer(), "key-2");
        string last = "";
        for (var i = 0; i < UploadTickets.MaxPerHolder; i++) last = Issue(Bearer());

        Assert.Null(_tickets.Take(firstToken));
        Assert.All(first, b => Assert.Equal(0, b));
        Assert.NotNull(_tickets.Take(last));
        Assert.NotNull(_tickets.Take(other));
    }

    [Fact]
    public void Take_is_atomic()
    {
        var token = Issue(Bearer());

        var taken = new UploadTicket?[50];
        Parallel.For(0, taken.Length, i => taken[i] = _tickets.Take(token));

        Assert.Equal(1, taken.Count(t => t is not null));
    }

    [Fact]
    public void Tokens_are_43_url_safe_characters()
    {
        var a = Issue(Bearer());
        var b = Issue(Bearer());

        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), a);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_credential_that_expires_sooner_shortens_the_ticket()
    {
        var soon = _time.GetUtcNow().UtcDateTime.AddMinutes(2);
        var (token, expiresAt) = _tickets.Issue(Bearer(), null, "/Work/x.png", null, "key-1", soon);

        Assert.Equal(soon, expiresAt);
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(_tickets.Take(token));

        var (_, normal) = _tickets.Issue(Bearer(), null, "/Work/x.png", null, "key-1", _time.GetUtcNow().UtcDateTime.AddHours(1));
        Assert.Equal(_time.GetUtcNow().UtcDateTime + UploadTickets.Lifetime, normal);
    }
}
