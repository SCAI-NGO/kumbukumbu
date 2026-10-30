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

    /// <summary>The failed tries the vault's one job has counted.</summary>
    private async Task<int> TriesAsync(string vaultId) =>
        (await Raw("pdf_jobs").Find(new BsonDocument("vault", vaultId)).SingleAsync(Ct)).GetValue("tries", 0).ToInt32();

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
        // A sidecar that is away is no fault of the PDF's: it counts no try.
        Assert.Equal(0, await TriesAsync(vault.Id));

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
    public async Task A_pdf_the_sidecar_keeps_going_down_on_is_given_up_after_5_tries()
    {
        // An out-of-memory restarts the sidecar: the connection breaks after the file was sent, every round.
        var sidecar = new FakePdfSidecar { FailWith = SidecarException.Aborted };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        await user.CreateFileAsync("heavy.pdf", Pdf("a paper that runs the sidecar out of memory"));
        var vault = await VaultAsync(host, user);

        for (var i = 0; i < 8 && await JobsAsync(vault.Id) > 0; i++) await runner.RoundAsync(Ct);

        Assert.Equal(0, await JobsAsync(vault.Id));
        Assert.Equal(PdfAnalysisRunner.MaxTries, sidecar.Analyses);
        var state = await StateAsync(host, user, "heavy.pdf");
        var stored = await host.Services.GetRequiredService<PdfAnalyses>().ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct);
        Assert.Equal("this PDF couldn't be analysed: the analyser stopped on it every time (it may run out of memory)", stored!.Failure!.Message);
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
    public async Task An_analysis_that_cant_be_stored_counts_a_try_and_is_given_up_after_5()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var analyses = host.Services.GetRequiredService<PdfAnalyses>();
        // As a database that refuses the write would (too large for its cache, say).
        analyses.StoreFaultAt = _ => throw new MongoException("the write is refused");
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        await user.CreateFileAsync("unstorable.pdf", Pdf("an analysis the database refuses"));
        var vault = await VaultAsync(host, user);

        for (var i = 0; i < 8 && await JobsAsync(vault.Id) > 0; i++) await runner.RoundAsync(Ct);

        Assert.Equal(0, await JobsAsync(vault.Id));
        Assert.Equal(PdfAnalysisRunner.MaxTries, sidecar.Analyses);
        var state = await StateAsync(host, user, "unstorable.pdf");
        var stored = await analyses.ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct);
        Assert.Equal("this PDF couldn't be analysed: its analysis couldn't be stored", stored!.Failure!.Message);
        // The text parts of the failed stores went with the failure's.
        Assert.Equal(1, await Raw("pdf_analyses").CountDocumentsAsync(new BsonDocument("vault", vault.Id), cancellationToken: Ct));
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

    [Fact]
    public async Task An_outage_uses_up_no_tries_so_a_broken_connection_after_it_counts_one()
    {
        // A deploy: the sidecar away for several rounds, then the connection breaks once as it goes down again.
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        await user.CreateFileAsync("paper.pdf", Pdf("a paper caught in a deploy"));
        var vault = await VaultAsync(host, user);

        for (var i = 0; i < PdfAnalysisRunner.MaxTries + 2; i++) await runner.RoundAsync(Ct);
        Assert.Equal(0, await TriesAsync(vault.Id));

        (sidecar.Down, sidecar.FailWith) = (false, SidecarException.Aborted);
        await runner.RoundAsync(Ct);

        Assert.Equal(1, await JobsAsync(vault.Id));
        Assert.Equal(1, await TriesAsync(vault.Id));
        Assert.Equal(0, await AnalysesAsync(vault.Id));
    }

    [Fact]
    public async Task A_busy_or_absent_sidecar_counts_no_try_and_the_analysis_runs_once_it_answers()
    {
        var sidecar = new FakePdfSidecar { FailWith = SidecarException.Busy };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        await user.CreateFileAsync("paper.pdf", Pdf("a paper the sidecar was too busy for"));
        var vault = await VaultAsync(host, user);

        for (var i = 0; i < PdfAnalysisRunner.MaxTries + 2; i++) await runner.RoundAsync(Ct);
        (sidecar.FailWith, sidecar.Down) = (null, true);
        for (var i = 0; i < PdfAnalysisRunner.MaxTries + 2; i++) await runner.RoundAsync(Ct);
        Assert.Equal(0, await TriesAsync(vault.Id));

        sidecar.Down = false;
        await runner.RoundAsync(Ct);

        Assert.Equal(0, await JobsAsync(vault.Id));
        Assert.Equal(1, await AnalysesAsync(vault.Id));
        var state = await StateAsync(host, user, "paper.pdf");
        var stored = await host.Services.GetRequiredService<PdfAnalyses>().ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct);
        Assert.Null(stored!.Failure);
        Assert.Equal(3, stored.Analysis!.PageCount);
    }

    /// <summary>A sweep as at <paramref name="now"/>, with the grace period of new blobs long over.</summary>
    private static Task SweepAsync(WebApplicationFactory<Program> host, UnlockedVault vault, DateTime now) =>
        new BlobSweep(host.Services.GetRequiredService<VaultCatalog>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RunAsync(vault, now, Ct);

    [Fact]
    public async Task Trashing_a_pdf_and_its_trash_expiring_take_its_analysis_and_job_along()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("analysed.pdf", Pdf("analysed, then trashed"));
        var vault = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the analysis is stored");
        sidecar.Down = true;
        await user.CreateFileAsync("pending.pdf", Pdf("queued, then trashed"));
        Assert.Equal(1, await JobsAsync(vault.Id));
        foreach (var name in new[] { "analysed.pdf", "pending.pdf" })
        {
            using var removed = await user.SendAsync(HttpMethod.Delete, user.Url(name, "files"));
            Assert.True(removed.IsSuccessStatusCode);
        }

        // In the trash, the blobs are still referenced: everything stays.
        await SweepAsync(host, vault, DateTime.UtcNow.AddHours(1));
        Assert.Equal((1, 1), (await AnalysesAsync(vault.Id), await JobsAsync(vault.Id)));

        await SweepAsync(host, vault, DateTime.UtcNow + Kumbu.Core.Notes.NoteWriter.TrashRetention + TimeSpan.FromHours(1));

        Assert.Equal(0, await Raw("blobs").CountDocumentsAsync(new BsonDocument("vault", vault.Id), cancellationToken: Ct));
        Assert.Equal(0, await Raw("pdf_analyses").CountDocumentsAsync(new BsonDocument("vault", vault.Id), cancellationToken: Ct));
        Assert.Equal(0, await JobsAsync(vault.Id));
    }

    [Fact]
    public async Task A_sweep_deletes_analyses_and_jobs_whose_blob_is_gone_and_keeps_the_others()
    {
        await using var host = Host(app, new FakePdfSidecar());
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("kept.pdf", Pdf("still here"));
        var vault = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the analysis is stored");
        // Left behind by a process that stopped half way, say.
        var orphan = new BsonBinaryData(new byte[32]);
        await Raw("pdf_analyses").InsertOneAsync(new BsonDocument { ["vault"] = vault.Id, ["bid"] = orphan, ["part"] = 0, ["blob"] = new BsonBinaryData([1]), ["at"] = DateTime.UtcNow }, cancellationToken: Ct);
        await Raw("pdf_jobs").InsertOneAsync(new BsonDocument { ["vault"] = vault.Id, ["bid"] = orphan, ["at"] = DateTime.UtcNow, ["tries"] = 0 }, cancellationToken: Ct);

        await SweepAsync(host, vault, DateTime.UtcNow);

        // Part 0 and the text part of the analysis left.
        Assert.Equal(2, await Raw("pdf_analyses").CountDocumentsAsync(new BsonDocument("vault", vault.Id), cancellationToken: Ct));
        Assert.Equal(0, await JobsAsync(vault.Id));
        var state = await StateAsync(host, user, "kept.pdf");
        Assert.NotNull(await host.Services.GetRequiredService<PdfAnalyses>().HeaderAsync(vault, Convert.FromBase64String(state.BlobId), Ct));
    }

    [Fact]
    public async Task Deleting_the_vault_deletes_its_analyses_and_jobs()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("analysed.pdf", Pdf("analysed, then the vault goes"));
        var vault = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(vault.Id) == 1 && await JobsAsync(vault.Id) == 0, "the analysis is stored");
        sidecar.Down = true;
        await user.CreateFileAsync("pending.pdf", Pdf("queued, then the vault goes"));

        Assert.NotNull(await host.Services.GetRequiredService<Kumbu.Server.Accounts.Deletion>().DeleteVaultAsync(user.User, user.UserKey, user.Vault, RestClient.Password, user.Vault, Ct));

        Assert.Equal(0, await Raw("pdf_analyses").CountDocumentsAsync(new BsonDocument("vault", vault.Id), cancellationToken: Ct));
        Assert.Equal(0, await JobsAsync(vault.Id));
    }

    [Fact]
    public async Task Rekeying_reseals_the_analysis_under_the_new_key_and_the_blobs_new_id_and_requeues_jobs()
    {
        var sidecar = new FakePdfSidecar { Analysis = _ => FakePdfSidecar.Canned(120, n => $"Seite {n}: " + new string('x', 30_000)) };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("analysed.pdf", Pdf("analysed before the re-keying"));
        var before = await VaultAsync(host, user);
        await EventuallyAsync(async () => await AnalysesAsync(before.Id) == 1 && await JobsAsync(before.Id) == 0, "the analysis is stored");
        sidecar.Down = true;
        await user.CreateFileAsync("pending.pdf", Pdf("queued before the re-keying"));
        var (analysed, pending) = (await StateAsync(host, user, "analysed.pdf"), await StateAsync(host, user, "pending.pdf"));
        var parts = await Raw("pdf_analyses").CountDocumentsAsync(new BsonDocument("vault", before.Id), cancellationToken: Ct);
        Assert.True(parts > 2);

        await host.Services.GetRequiredService<VaultCatalog>().RekeyAsync(before.Id, user.User.Id, user.UserKey, Ct);

        var after = await VaultAsync(host, user);
        var (analysedNow, pendingNow) = (await StateAsync(host, user, "analysed.pdf"), await StateAsync(host, user, "pending.pdf"));
        Assert.NotEqual(analysed.BlobId, analysedNow.BlobId);
        var analyses = host.Services.GetRequiredService<PdfAnalyses>();
        var header = await analyses.HeaderAsync(after, Convert.FromBase64String(analysedNow.BlobId), Ct);
        Assert.Equal(120, header!.PageCount);
        Assert.Equal(["Seite 77: " + new string('x', 30_000)], await analyses.TextAsync(after, Convert.FromBase64String(analysedNow.BlobId), 77, 77, Ct));
        var docs = await Raw("pdf_analyses").Find(new BsonDocument("vault", before.Id)).ToListAsync(Ct);
        Assert.Equal(parts, docs.Count);
        Assert.All(docs, d => Assert.Equal(analysedNow.BlobId, Convert.ToBase64String(d["bid"].AsByteArray)));
        // The old key opens none of it any more.
        Assert.Null(await analyses.ReadAsync(before, Convert.FromBase64String(analysedNow.BlobId), Ct));
        Assert.Null(await analyses.ReadAsync(before, Convert.FromBase64String(analysed.BlobId), Ct));

        // The job of the PDF not analysed yet is queued again under its blob's new id.
        await EventuallyAsync(
            async () => (await Raw("pdf_jobs").Find(new BsonDocument("vault", before.Id)).ToListAsync(Ct)) is [var job] && Convert.ToBase64String(job["bid"].AsByteArray) == pendingNow.BlobId,
            "the job is queued under the new id");
        sidecar.Down = false;
        host.Services.GetRequiredService<PdfAnalysisRunner>().Wake();
        await EventuallyAsync(async () => await AnalysesAsync(before.Id) == 2 && await JobsAsync(before.Id) == 0, "the second analysis is stored");
    }
}
