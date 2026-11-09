// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Search;

/// <summary>
/// Structural chunking end to end (spec §5): a note with a long table and a PDF with a dense page are stored,
/// chunked, embedded and found by <c>semsearch</c>, which prints the core that holds the match. With the hashing
/// embedder by shared words; with the real model (<c>KUMBU_MODEL_DIR</c>) by meaning, from a paraphrase.
/// </summary>
[Collection("app")]
public sealed class StructuralChunkingEndToEndTests(KumbuAppFactory app)
{
    private const string Header = "| Code | Meaning | Remedy |";

    private const string Flowers = "Cowslips, poppies and cornflowers bloom along the meadow path in early summer, and bees visit the oxeye daisies.";

    /// <summary>The spec's example (§3.9): E001 to E240, then E241, the only row about sensor drift.</summary>
    private static readonly string PumpManual = Build(text =>
    {
        string[] meanings = ["Dry run", "Inlet blocked", "Seal leaking", "Motor overheated", "Low pressure", "Valve stuck", "Air in the line", "Bearing worn", "Filter clogged"];
        string[] remedies = ["Fill the inlet and restart", "Clear the inlet strainer", "Replace the mechanical seal", "Let the motor cool and check the fan",
            "Check the suction line for leaks", "Free the valve and grease the spindle", "Vent the pump at the top screw", "Replace both bearings", "Clean or replace the filter"];
        text.Append("# Pump P-200\n## Safety\nWear gloves. Disconnect power before opening the housing.\n## Error codes\n");
        text.Append(Header).Append("\n|------|---------|--------|\n");
        for (var i = 1; i <= 240; i++) text.Append($"| E{i:000} | {meanings[i % 9]} at stage {i % 7 + 1} | {remedies[i % 9]} |\n");
        text.Append("| E241 | Sensor drift | Recalibrate in the service menu |\n");
        text.Append("## Maintenance\n### Weekly\nCheck the seal for drips.\n### Monthly\nClean the filter.\n");
    });

    /// <summary>A page of about 9,000 characters in numbered lines of German prose, paragraphs of four, ending with a
    /// paragraph about wild flowers.</summary>
    private static readonly string DensePage = Build(text =>
    {
        string[] sentences =
        [
            "Die Kreiselpumpe fördert Kühlwasser in den Wärmetauscher.",
            "Vor dem Öffnen des Gehäuses ist die Anlage spannungsfrei zu schalten.",
            "Der Druck am Auslass wird täglich abgelesen und ins Betriebsbuch eingetragen.",
            "Undichte Gleitringdichtungen sind sofort zu tauschen.",
        ];
        for (var i = 0; text.Length < 9_000; i++)
        {
            text.Append($"{i + 1}. {sentences[i % sentences.Length]} Messwert {i * 37 % 1000},{i % 10} bar bei {1450 + i} 1/min.\n");
            if (i % 4 == 3) text.Append('\n');
        }

        text.Append('\n').Append(Flowers).Append('\n');
    });

    private static string Build(Action<StringBuilder> build)
    {
        var text = new StringBuilder();
        build(text);
        return text.ToString();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The model, where it has been downloaded; null otherwise.</summary>
    private static string? ModelDir() =>
        Environment.GetEnvironmentVariable("KUMBU_MODEL_DIR") is { Length: > 0 } dir && File.Exists(Path.Combine(dir, ModelFiles.Default, ModelFiles.Model)) ? dir : null;

    /// <summary>The app with the canned page texts; with <paramref name="real"/> the real model embeds, from
    /// <c>KUMBU_MODEL_DIR</c>, which also holds the tokenizer that bounds the chunks.</summary>
    private WebApplicationFactory<Program> Host(bool real)
    {
        var sidecar = new FakePdfSidecar { Analysis = _ => FakePdfSidecar.Canned(2, n => n == 1 ? DensePage : "filler page of plain words") };
        var host = PdfJobTests.Host(app, sidecar);
        if (!real) return host;
        return host.WithWebHostBuilder(b =>
        {
            b.UseSetting("Kumbu:Search:Enabled", "true");
            b.UseSetting("Kumbu:Search:ModelPath", ModelDir()!);
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IEmbeddingDriver>();
                services.AddSingleton<IEmbeddingDriver>(sp => sp.GetRequiredService<OnnxEmbedder>());
                // The files are verified, never fetched.
                services.AddHttpClient(nameof(OnnxEmbedder)).ConfigurePrimaryHttpMessageHandler(() => new NoDownload());
            });
        });
    }

    /// <summary>The app with the real model loaded; skipped without it.</summary>
    private async Task<WebApplicationFactory<Program>> RealHostAsync()
    {
        Assert.SkipUnless(ModelDir() is not null, "KUMBU_MODEL_DIR with the model not set");
        var host = Host(real: true);
        try
        {
            var model = Assert.IsType<OnnxEmbedder>(host.Services.GetRequiredService<IEmbeddingDriver>());
            await model.WhenReadyAsync(Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static UnlockedVault Current(WebApplicationFactory<Program> host, UnlockedVault vault) =>
        host.Services.GetRequiredService<VaultCatalog>().Current(vault.Id) ?? vault;

    /// <summary>Waits until <paramref name="path"/> is embedded at <paramref name="version"/> (any, if null); the real
    /// model takes its time on a CPU.</summary>
    private static async Task EmbeddedAsync(WebApplicationFactory<Program> host, UnlockedVault vault, string path, string? version = null)
    {
        var until = DateTime.UtcNow.AddMinutes(3);
        while (Current(host, vault).Embeddings.GetValueOrDefault(P(path)) is not { } e || version is not null && e.Version != version)
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting until {path} is embedded");
            await Task.Delay(100, Ct);
        }
    }

    private static async Task<(RestClient User, UnlockedVault Vault, string Key)> UserAsync(WebApplicationFactory<Program> host)
    {
        var user = await RestClient.NewAsync(host, vault: "Work");
        var vault = await VaultAsync(host, user);
        return (user, vault, await user.KeyAsync("search", KeyRight.Write));
    }

    private static async Task<(RestClient User, string Key)> ManualAsync(WebApplicationFactory<Program> host)
    {
        var (user, vault, key) = await UserAsync(host);
        var version = await user.CreateAsync("Pump/Manual.md", PumpManual);
        await EmbeddedAsync(host, vault, "/Work/Pump/Manual.md", version);
        return (user, key);
    }

    private static async Task<(RestClient User, UnlockedVault Vault, string Key)> PdfAsync(WebApplicationFactory<Program> host)
    {
        var (user, vault, key) = await UserAsync(host);
        await user.CreateFileAsync("Pump/P-200.pdf", Pdf("the pump's manual"));
        await EmbeddedAsync(host, vault, "/Work/Pump/P-200.pdf");
        return (user, vault, key);
    }

    /// <summary>The hits of <c>semsearch</c>'s output, each its header line and the lines below it.</summary>
    private static List<string> Hits(string output) =>
        [.. Regex.Split(output, @"^(?=\d+\. /)", RegexOptions.Multiline).Where(h => h.Length > 0 && char.IsDigit(h[0]))];

    /// <summary>The best hit on <paramref name="path"/>, without its number.</summary>
    private static string Hit(string output, string path) =>
        Hits(output).Select(h => h[(h.IndexOf(' ') + 1)..]).First(h => h.StartsWith(path + " ", StringComparison.Ordinal) || h.StartsWith(path + "#", StringComparison.Ordinal));

    private static void TableRowFound(string output)
    {
        var first = Hits(output)[0];
        Assert.StartsWith("1. /Work/Pump/Manual.md#Pump P-200#Error codes  lines ", first);
        Assert.Contains("\n   " + Header + "\n   |------|---------|--------|\n", first);
        Assert.Contains("\n   | E241 | Sensor drift | Recalibrate in the service menu |\n", first);
        Assert.DoesNotContain("| E001 |", first);
    }

    private static void PageFound(string output)
    {
        var page = Hit(output, "/Work/Pump/P-200.pdf");
        Assert.StartsWith("/Work/Pump/P-200.pdf p1  score ", page);
        Assert.Contains("\n   " + Flowers + "\n", page);
        // The page's last part, not its first.
        Assert.DoesNotContain("\n   1. Die Kreiselpumpe", page);
    }

    private static void NoteFound(string output)
    {
        var note = Hit(output, "/Work/Notes/Pump page.md");
        Assert.StartsWith("/Work/Notes/Pump page.md#P-200, page 1  lines ", note);
        Assert.Contains("\n   " + Flowers + "\n", note);
    }

    /// <summary>As the ingest skill does: the page's text written into a note, beside the PDF.</summary>
    private static async Task NoteAsync(WebApplicationFactory<Program> host, RestClient user, UnlockedVault vault)
    {
        var version = await user.CreateAsync("Notes/Pump page.md", "# P-200, page 1\n\n" + DensePage);
        await EmbeddedAsync(host, vault, "/Work/Notes/Pump page.md", version);
    }

    [Fact]
    public async Task A_row_near_the_end_of_a_long_table_is_found_with_its_header()
    {
        await using var host = Host(real: false);
        var (user, key) = await ManualAsync(host);

        var (isError, output) = await user.ShAsync("semsearch sensor drift recalibrate /Work", key);

        Assert.False(isError, output);
        TableRowFound(output);
    }

    [Fact]
    public async Task A_sentence_at_the_end_of_a_dense_page_is_found()
    {
        await using var host = Host(real: false);
        var (user, _, key) = await PdfAsync(host);

        var (isError, output) = await user.ShAsync("semsearch cowslips poppies cornflowers meadow /Work", key);

        Assert.False(isError, output);
        Assert.StartsWith("1. /Work/Pump/P-200.pdf p1  score ", output);
        PageFound(output);
    }

    [Fact]
    public async Task The_same_content_written_into_a_note_is_found()
    {
        await using var host = Host(real: false);
        var (user, vault, key) = await PdfAsync(host);
        await NoteAsync(host, user, vault);

        var (isError, output) = await user.ShAsync("semsearch cowslips poppies cornflowers meadow /Work/Notes", key);

        Assert.False(isError, output);
        Assert.StartsWith("1. /Work/Notes/Pump page.md#", output);
        NoteFound(output);
    }

    [Fact]
    public async Task The_real_model_finds_a_table_row_by_meaning()
    {
        await using var host = await RealHostAsync();
        var (user, key) = await ManualAsync(host);

        var (isError, output) = await user.ShAsync("semsearch --meaning the measuring probe reads wrong values, how do I adjust it /Work", key);

        Assert.False(isError, output);
        TableRowFound(output);
    }

    [Fact]
    public async Task The_real_model_finds_the_end_of_a_dense_page_by_meaning()
    {
        await using var host = await RealHostAsync();
        var (user, _, key) = await PdfAsync(host);

        var (isError, output) = await user.ShAsync("semsearch --meaning which wild plants blossom beside the field track /Work", key);

        Assert.False(isError, output);
        Assert.StartsWith("1. /Work/Pump/P-200.pdf p1  score ", output);
        PageFound(output);
    }

    [Fact]
    public async Task The_real_model_finds_the_same_content_in_a_note_by_meaning()
    {
        await using var host = await RealHostAsync();
        var (user, vault, key) = await PdfAsync(host);
        await NoteAsync(host, user, vault);

        var (isError, output) = await user.ShAsync("semsearch --meaning which wild plants blossom beside the field track /Work", key);

        Assert.False(isError, output);
        // The note and the page it came from, ahead of everything else.
        var top = Hits(output).Take(2).Select(h => h[(h.IndexOf(' ') + 1)..]).ToList();
        Assert.Contains(top, h => h.StartsWith("/Work/Notes/Pump page.md#", StringComparison.Ordinal));
        Assert.Contains(top, h => h.StartsWith("/Work/Pump/P-200.pdf p1 ", StringComparison.Ordinal));
        NoteFound(output);
        PageFound(output);
    }
}
