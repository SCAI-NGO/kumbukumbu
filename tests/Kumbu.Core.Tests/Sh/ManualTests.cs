// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

/// <summary>The MCP tool descriptions: constants that feed [Description], so their numbers are pinned to the code's here.</summary>
public class ManualTests
{
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    private static string Line(string start) => Manual.ToolDescription.Split('\n').Single(l => l.StartsWith(start, StringComparison.Ordinal));

    [Fact]
    public void Manual_rg_files_lists_notes_and_text_files() =>
        Assert.Equal("  rg --files [path...]            list note and text file paths; find a note by name: rg --files | rg -i kumbu", Line("  rg --files "));

    [Fact]
    public void Manual_rg_searches_notes_text_files_and_PDF_text() =>
        Assert.Equal("  rg [options] <regex> [path...]  search notes, text files and PDF text, prints path:line:text; no path searches every vault; -g GLOB filters paths", Line("  rg [options] "));

    [Fact]
    public void Manual_says_rg_searches_text_files_in_a_checkpoint()
    {
        Assert.Contains("rg searches notes and text files there, not PDFs.", Line("Checkpoints: "));
        Assert.DoesNotContain("rg searches notes only there", Manual.ToolDescription);
    }

    [Fact]
    public void Manual_rm_trash_and_mv_name_attachments()
    {
        Assert.Equal("  rm <path> [<path>…]             move notes, attachments or empty folders to the trash (kept 30 days)", Line("  rm "));
        Assert.Equal("  trash [path]                    list removed notes, attachments and folders", Line("  trash "));
        Assert.Equal("  mv <path>… <target>             move or rename notes, attachments or folders (/Work/old/); links are rewritten", Line("  mv "));
    }

    [Fact]
    public void Manual_write_says_what_it_takes()
    {
        var text = Flat(Manual.WriteDescription);
        Assert.DoesNotContain("The path must end in .md", text);
        Assert.Contains("The path names a note (ending in .md), an SVG (.svg) or a text file in a skill's folder (a script, say), in an existing vault (sh: ls); missing directories are fine. It fails if the path exists. Any other file goes in with the upload tool.", text);
    }

    [Fact]
    public void Manual_upload_says_an_SVG_goes_either_way()
    {
        var text = Flat(Manual.UploadDescription);
        Assert.Contains("Notes and a skill's text files are written with the write tool, an SVG with either tool; everything else is uploaded here.", text);
        Assert.DoesNotContain("The write tool writes notes, SVGs and a skill's text files", text);
    }

    [Fact]
    public void Manual_trash_retention_is_the_constants() =>
        Assert.Contains($"(kept {NoteWriter.TrashRetention.Days} days)", Manual.ToolDescription);

    [Fact]
    public void Manual_output_limit_is_the_constants() =>
        Assert.Contains($"about {OutputLimit.MaxChars / OutputLimit.CharsPerToken} tokens per call", Manual.ToolDescription);

    [Fact]
    public void Manual_upload_size_is_the_constants() =>
        Assert.Contains($"file larger than {AttachmentFormat.MaxBytes / (1024 * 1024)} MB is kept, but not read", Flat(Manual.UploadDescription));

    [Fact]
    public void Manual_replace_plan_lifetime_is_the_constants()
    {
        Assert.Equal(TimeSpan.FromHours(1), Plans.Lifetime);
        Assert.Contains("A plan is valid for one hour and once.", Flat(Manual.ReplaceDescription));
    }
}
