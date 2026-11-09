// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Api;
using static Kumbu.Server.Tests.Attachments.PdfImagesToolTests;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>One <c>pdfimages</c> call per account at a time, over MCP: a second call of the same user waits for nothing and is refused.</summary>
[Collection("app")]
public sealed class ExtractionGateTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string StillRunning = "pdfimages: another pdfimages call of yours is still running; try again when it has finished";

    /// <summary>A sidecar whose listing of a page holds until let go, so a call stays under way.</summary>
    private sealed class HeldSidecar : IDisposable
    {
        public readonly SemaphoreSlim Entered = new(0);
        public readonly ManualResetEventSlim Go = new(false);

        public FakePdfSidecar Sidecar { get; }

        public HeldSidecar() => Sidecar = new FakePdfSidecar
        {
            PageImages = (_, page) =>
            {
                Entered.Release();
                Go.Wait(TimeSpan.FromSeconds(30));
                return FakePdfSidecar.CannedImages(page, 1);
            },
        };

        public void Dispose()
        {
            Go.Set();
            Entered.Dispose();
        }
    }

    private static async Task<(RestClient User, string Key)> UserWithPdfAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("x.pdf", Pdf("one page with a photo"));
        return (user, await user.KeyAsync("pdfimages", KeyRight.Write));
    }

    [Fact]
    public async Task A_second_call_of_the_same_user_is_refused_while_the_first_runs()
    {
        using var held = new HeldSidecar();
        await using var host = Host(app, held.Sidecar);
        var (user, key) = await UserWithPdfAsync(host);
        // A second credential of the same account shares its place.
        var other = await user.KeyAsync("another agent", KeyRight.Write);
        var vault = $"/{user.Vault}";

        var first = ShAsync(host, key, $"pdfimages --list {vault}/x.pdf");
        Assert.True(await held.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var second = await ShAsync(host, other, $"pdfimages {vault}/x.pdf {vault}/images/");
        held.Go.Set();
        var answered = await first;

        Assert.True(second.IsError);
        Assert.Equal(StillRunning, Text(second));
        Assert.False(answered.IsError, Text(answered));
        var after = await ShAsync(host, key, $"pdfimages --list {vault}/x.pdf");
        Assert.False(after.IsError, Text(after));
    }

    [Fact]
    public async Task Calls_of_different_users_run_side_by_side()
    {
        using var held = new HeldSidecar();
        await using var host = Host(app, held.Sidecar);
        var (alice, aliceKey) = await UserWithPdfAsync(host);
        var (bob, bobKey) = await UserWithPdfAsync(host);

        var first = ShAsync(host, aliceKey, $"pdfimages --list /{alice.Vault}/x.pdf");
        Assert.True(await held.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var second = ShAsync(host, bobKey, $"pdfimages --list /{bob.Vault}/x.pdf");
        Assert.True(await held.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        held.Go.Set();

        foreach (var result in await Task.WhenAll(first, second)) Assert.False(result.IsError, Text(result));
    }

    [Fact]
    public void A_place_is_the_accounts_until_given_back_once()
    {
        var gate = new ExtractionGate();
        var alice = gate.TryStart("alice");
        Assert.NotNull(alice);
        Assert.Null(gate.TryStart("alice"));
        using (var bob = gate.TryStart("bob")) Assert.NotNull(bob);

        alice.Dispose();
        var again = gate.TryStart("alice");
        Assert.NotNull(again);
        alice.Dispose();
        Assert.Null(gate.TryStart("alice"));
        again.Dispose();
    }
}
