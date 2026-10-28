// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>The analysis queue: a PDF upload leaves a job, which runs while the vault is unlocked and the sidecar is up,
/// survives a restart, and goes with the blob.</summary>
[Collection("app")]
public sealed class PdfJobTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A server of its own on the app's database, with <paramref name="sidecar"/> for its PDF sidecar.</summary>
    internal static WebApplicationFactory<Program> Host(KumbuAppFactory app, FakePdfSidecar sidecar) => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<IPdfSidecar>();
        s.AddSingleton<IPdfSidecar>(sidecar);
    }));

    internal static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.7\n% {text}\n%%EOF\n");

    private IMongoCollection<BsonDocument> Raw(string name) => app.Database.GetCollection<BsonDocument>(name);

    private Task<long> JobsAsync(string vaultId) => Raw("pdf_jobs").CountDocumentsAsync(new BsonDocument("vault", vaultId), cancellationToken: Ct);

    private Task<long> AnalysesAsync(string vaultId) =>
        Raw("pdf_analyses").CountDocumentsAsync(new BsonDocument { ["vault"] = vaultId, ["part"] = 0 }, cancellationToken: Ct);

    internal static async Task<UnlockedVault> VaultAsync(WebApplicationFactory<Program> host, RestClient user) =>
        (await host.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);

    internal static async Task<AttachmentState> StateAsync(WebApplicationFactory<Program> host, RestClient user, string path) =>
        (await new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey))
            .GetAttachmentAsync(P($"/{user.Vault}/{path}"), Ct))!.Current;

    internal static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting until {what}");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task An_uploaded_pdf_is_analysed_and_its_job_removed()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);

        await user.CreateFileAsync("paper.pdf", Pdf("an uploaded paper"));

        var vault = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the analysis is stored");
        var state = await StateAsync(host, user, "paper.pdf");
        var header = await host.Services.GetRequiredService<PdfAnalyses>().HeaderAsync(vault, Convert.FromBase64String(state.BlobId), Ct);
        Assert.Equal((3, 3, "A title"), (header!.PageCount, header.AnalysedPages, header.Title));
        Assert.Equal(1, sidecar.Analyses);
    }

    [Fact]
    public async Task While_the_sidecar_is_down_the_job_stays_and_runs_once_it_is_back()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();

        await user.CreateFileAsync("paper.pdf", Pdf("waiting for the sidecar"));
        var vault = await VaultAsync(host, user);
        await runner.RoundAsync(Ct);

        Assert.Equal(1, await JobsAsync(vault.Id));
        Assert.Equal(0, await AnalysesAsync(vault.Id));
        var job = await Raw("pdf_jobs").Find(new BsonDocument("vault", vault.Id)).SingleAsync(Ct);
        Assert.True(job["tries"].AsInt32 >= 1);

        sidecar.Down = false;
        runner.Wake();

        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the analysis is stored");
    }

    [Fact]
    public async Task A_restarted_server_finds_the_job_and_runs_it_once_the_vault_is_unlocked()
    {
        var down = new FakePdfSidecar { Down = true };
        RestClient user;
        string vaultId;
        await using (var before = Host(app, down))
        {
            user = await RestClient.NewAsync(before);
            await user.CreateFileAsync("paper.pdf", Pdf("before the restart"));
            vaultId = (await VaultAsync(before, user)).Id;
            Assert.Equal(1, await JobsAsync(vaultId));
        }

        var up = new FakePdfSidecar();
        await using var after = Host(app, up);
        _ = after.Services;
        await Task.Delay(300, Ct);
        // Locked: nothing to analyse with.
        Assert.Equal(0, up.Analyses);
        Assert.Equal(1, await JobsAsync(vaultId));

        await VaultAsync(after, user);

        await EventuallyAsync(async () => await AnalysesAsync(vaultId) == 1 && await JobsAsync(vaultId) == 0, "the analysis is stored");
        Assert.Equal(1, up.Analyses);
    }

    [Fact]
    public async Task The_same_bytes_under_another_name_add_no_job_and_no_analysis()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var bytes = Pdf("one paper, two names");
        await user.CreateFileAsync("paper.pdf", bytes);
        var vault = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the analysis is stored");

        await user.CreateFileAsync("copy/same.pdf", bytes);
        await host.Services.GetRequiredService<PdfAnalysisRunner>().RoundAsync(Ct);

        Assert.Equal(0, await JobsAsync(vault.Id));
        Assert.Equal(1, await AnalysesAsync(vault.Id));
        Assert.Equal(1, sidecar.Analyses);
    }

    [Fact]
    public async Task An_image_leaves_no_job()
    {
        await using var host = Host(app, new FakePdfSidecar());
        var user = await RestClient.NewAsync(host);

        await user.CreateFileAsync("shot.png", FakePdfSidecar.SolidPng(8, 8));

        Assert.Equal(0, await JobsAsync((await VaultAsync(host, user)).Id));
    }

    [Fact]
    public async Task A_password_protected_pdf_is_stored_as_such_and_not_tried_again()
    {
        var sidecar = new FakePdfSidecar { FailWith = SidecarException.Encrypted };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);

        await user.CreateFileAsync("locked.pdf", Pdf("a password-protected paper"));

        var vault = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the failure is stored");
        var state = await StateAsync(host, user, "locked.pdf");
        var stored = await host.Services.GetRequiredService<PdfAnalyses>().ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct);
        Assert.Equal(new PdfFailure("encrypted", "this PDF is password-protected; Kumbu² can't read it"), stored!.Failure);
        await host.Services.GetRequiredService<PdfAnalysisRunner>().RoundAsync(Ct);
        Assert.Equal(1, sidecar.Analyses);
    }

    [Fact]
    public async Task A_failure_that_keeps_coming_back_is_given_up_after_5_tries()
    {
        var sidecar = new FakePdfSidecar { FailWith = SidecarException.Timeout };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        await user.CreateFileAsync("slow.pdf", Pdf("a paper that always times out"));
        var vault = await VaultAsync(host, user);

        for (var i = 0; i < 8 && await JobsAsync(vault.Id) > 0; i++) await runner.RoundAsync(Ct);

        Assert.Equal(0, await JobsAsync(vault.Id));
        Assert.Equal(PdfAnalysisRunner.MaxTries, sidecar.Analyses);
        var state = await StateAsync(host, user, "slow.pdf");
        var stored = await host.Services.GetRequiredService<PdfAnalyses>().ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct);
        Assert.Equal("this PDF couldn't be analysed: it took longer than the time limit", stored!.Failure!.Message);
    }

    [Fact]
    public async Task An_unavailable_sidecar_is_never_given_up_on()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        await user.CreateFileAsync("paper.pdf", Pdf("the sidecar is away for long"));
        var vault = await VaultAsync(host, user);

        for (var i = 0; i < PdfAnalysisRunner.MaxTries + 2; i++) await runner.RoundAsync(Ct);

        Assert.Equal(1, await JobsAsync(vault.Id));
        Assert.Equal(0, await AnalysesAsync(vault.Id));
    }
}
