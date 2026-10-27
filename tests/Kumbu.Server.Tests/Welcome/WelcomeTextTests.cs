// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Links;
using Kumbu.Server.Welcome;

namespace Kumbu.Server.Tests.Welcome;

/// <summary>The welcome vault's texts: the seven notes, links that work in the web app and in Obsidian, and the
/// rules the texts keep (AI-agnostic, no deployer, the tested markers, the menu names they quote).</summary>
public sealed partial class WelcomeTextTests
{
    private const string ClaudeSection = "## This is how you connect Claude (claude.ai, Desktop and mobile)";
    private const string ClaudeCodeSection = "## This is how you connect Claude Code";
    private const string ChatGptSection = "## This is how you connect ChatGPT";
    private const string OtherSection = "## This is how you connect any other MCP client";

    private static readonly string[] Names =
        ["Connect your AI.md", "Obsidian.md", "Security and privacy.md", "Start here.md", "Working by hand.md", "Your AI in Kumbu².md", "Your data.md"];

    private static string Text(string name) => WelcomeVault.Texts().Single(t => t.Name == name).Content;

    /// <summary>The text from <paramref name="heading"/> to the next <c>## </c> heading.</summary>
    private static string Section(string content, string heading)
    {
        var start = content.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No section {heading}");
        var next = content.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? content[start..] : content[start..next];
    }

    private static IEnumerable<string> Headings(string content) =>
        HeadingRegex().Matches(content).Select(m => m.Groups[1].Value.Trim());

    [Fact]
    public void The_vault_holds_exactly_the_seven_notes()
    {
        Assert.Equal(Names, WelcomeVault.Texts().Select(t => t.Name));
    }

    [Fact]
    public void The_checkpoints_section_names_open_export_and_the_ai()
    {
        var section = Section(Text("Working by hand.md"), "## Checkpoints");
        Assert.Contains("**Open**", section, StringComparison.Ordinal);
        Assert.Contains("**Export as ZIP…**", section, StringComparison.Ordinal);
        Assert.Contains("**Copy for your AI**", section, StringComparison.Ordinal);
        Assert.Contains("--checkpoint", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_text_starts_with_its_title_and_links_back_to_start_here()
    {
        foreach (var (name, content) in WelcomeVault.Texts())
        {
            Assert.StartsWith($"# {name[..^3]}\n", content, StringComparison.Ordinal);
            if (name != "Start here.md") Assert.Contains("[[Start here]]", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_link_resolves_and_no_link_is_a_web_url()
    {
        var titles = Names.Select(n => n[..^3]).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, content) in WelcomeVault.Texts())
        {
            foreach (var link in WikiLink.Parse(content))
            {
                var target = link.Target.EndsWith(".md", StringComparison.Ordinal) ? link.Target[..^3] : link.Target;
                if (target.Length == 0) target = name[..^3];
                Assert.True(titles.Contains(target), $"{name}: {link.Raw} names no welcome note");
                if (link.Subpath.Length > 0)
                {
                    Assert.Contains(link.Subpath[1..], Headings(Text(target + ".md")));
                }
            }

            foreach (Match m in MarkdownLinkRegex().Matches(content))
            {
                var url = m.Groups[1].Value;
                Assert.False(url.Contains("/notes/", StringComparison.Ordinal), $"{name}: {url} is a web app URL");
                Assert.False(url.Contains(WelcomeVault.Placeholder, StringComparison.Ordinal), $"{name}: {url} points at the server");
                Assert.StartsWith("https://", url, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Start_here_links_every_other_note()
    {
        var start = Text("Start here.md");
        foreach (var link in new[] { "[[Working by hand]]", "[[Your AI in Kumbu²]]", "[[Obsidian]]", "[[Connect your AI]]", "[[Security and privacy]]", "[[Your data]]" })
        {
            Assert.Contains(link, start, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_texts_are_ai_agnostic_outside_the_client_sections()
    {
        string[] vendors = ["Claude", "ChatGPT", "GPT", "Anthropic", "OpenAI", "Gemini", "Google", "Copilot", "Microsoft", "Mistral", "Cursor", "Llama"];
        foreach (var (name, content) in WelcomeVault.Texts())
        {
            var outside = content;
            if (name == "Connect your AI.md")
            {
                outside = outside.Replace(Section(content, ClaudeSection), "", StringComparison.Ordinal)
                    .Replace(Section(content, ClaudeCodeSection), "", StringComparison.Ordinal)
                    .Replace(Section(content, ChatGptSection), "", StringComparison.Ordinal)
                    .Replace(Section(content, OtherSection), "", StringComparison.Ordinal);
            }

            foreach (var vendor in vendors) Assert.False(outside.Contains(vendor, StringComparison.Ordinal), $"{name} names {vendor}");
        }
    }

    [Fact]
    public void No_text_names_a_deployer_or_leaves_a_placeholder_unknown()
    {
        foreach (var (name, content) in WelcomeVault.Texts())
        {
            Assert.DoesNotContain("tidesson", content, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                content.Split("{{").Length - 1,
                content.Split(WelcomeVault.Placeholder).Length - 1);
        }
    }

    [Fact]
    public void No_text_promises_renaming_a_vault()
    {
        // Kumbu² renames notes and folders, never a vault.
        foreach (var (name, content) in WelcomeVault.Texts())
        {
            Assert.False(VaultRenameRegex().IsMatch(content), $"{name} promises renaming a vault");
        }
    }

    [Fact]
    public void Tested_markers_are_as_released()
    {
        var connect = Text("Connect your AI.md");
        Assert.Contains("*Tested.*", Section(connect, ClaudeCodeSection), StringComparison.Ordinal);
        Assert.Contains("*Tested.*", Section(connect, ClaudeSection), StringComparison.Ordinal);
        Assert.Contains("*Tested.*", Section(connect, ChatGptSection), StringComparison.Ordinal);
        Assert.Contains("*Tested with Grok and Mistral (a connection).*", Section(connect, OtherSection), StringComparison.Ordinal);
    }

    [Fact]
    public void The_menu_names_quoted_are_the_shared_labels()
    {
        // The exact labels the v0.2.0 plans agreed on ("Shared places"), and the web app's own.
        var all = string.Concat(WelcomeVault.Texts().Select(t => t.Content));
        foreach (var label in new[]
                 {
                     "Connect Obsidian…", "Export as ZIP", "Activity", "Settings → Your data", "Export my vaults…", "Import vaults…",
                     "drawer → Access", "Connect an app", "Search by meaning…", "Refresh Read mode automatically",
                     "Settings → Vaults", "Settings → **Install as an app**", "Settings → **About**", "New key",
                 })
        {
            Assert.Contains(label, all, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^#{1,6} (.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"renam\w* (the|this|your|a) (whole )?vault|vault\w* can be renamed", RegexOptions.IgnoreCase)]
    private static partial Regex VaultRenameRegex();
}
