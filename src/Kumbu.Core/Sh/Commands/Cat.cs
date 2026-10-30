// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class Cat(INoteReader notes, IAttachmentPixels? pixels = null) : ICommand
{
    public string Name => "cat";

    public string Usage => "cat [--version V] <path>[#section]";

    public string Summary => "print a note, or one section of it";

    public string Help => """
        cat <path>[#section]
          Print a note, or one section of it: the heading and everything below it up to the next
          heading of the same or a higher level. 'outline <path>' lists the sections.
          Address nested headings as #Parent#Child; a unique heading works on its own. Duplicate
          headings under one parent take an index, #Notes[2]. Write a literal '#' as \#.
          The version token comes separately, always for the whole note; pass it to 'edit' or 'write'.
          --version V   print an earlier version from 'history <path>' instead (no version token: an
                        earlier version can be compared with 'diff' and brought back with 'restore')
          An attachment (an image or a PDF) prints one line instead of its bytes: its kind, pixel size
          and file size ('cat /Work/shot.png' → shot.png: image (PNG), 1440×3120, 412 KB); for a PDF
          its pages and their types (paper.pdf: PDF, 12 pages (1-3 text, 4 mixed, 5-12 text), 2.1 MB).
          'help img' and 'help pdf' say how to look at them.
          A whole note too long for one call (about 8000 tokens) prints its outline instead; read it in
          parts with cat "<path>#Section", head -n 40 <path> or rg -n. In a pipeline, cat passes the
          whole note on (cat <path> | tail -n 50).

          cat /Demo/welcome.md
          cat "/Demo/welcome.md#Try it"
          cat "/Work/plan.md#Architecture#Gates" | head -n 20
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!CommandArgs.TryTakeVersion(Name, args, out var version, out var rest, out var versionError)) return versionError!;
        if (CommandArgs.CheckOptions(Name, rest) is { } optionError) return optionError;
        if (rest.Count == 0) return ShellResult.Error("cat: missing operand");
        if (rest.Count > 1) return ShellResult.Error($"cat: too many arguments{CommandArgs.QuoteHint}");

        var (pathText, _) = SectionAddress.Split(rest[0]);
        if (VaultPath.TryParse(pathText, out var path) && await StoreAttachments.GetAsync(notes, path, ct) is { } attachment)
        {
            return await DescribeAsync(path, attachment, version, ct);
        }

        // x.png#Layout: a section of an attachment, which has none (a '#' may also be part of a name, as in a#b.png).
        for (var at = rest[0].IndexOf('#'); at > 0; at = rest[0].IndexOf('#', at + 1))
        {
            if (VaultPath.TryParse(rest[0][..at], out var file) && file.IsAttachment && await StoreAttachments.GetAsync(notes, file, ct) is not null)
            {
                return ShellResult.Error($"cat: {file.Value} is an attachment, which has no sections; 'cat {SectionLookup.Quote(file)}' describes it");
            }
        }

        return await CommandArgs.ReadAddressAsync(Name, notes, rest[0], ct, version);
    }

    private async Task<ShellResult> DescribeAsync(VaultPath path, StoredAttachment attachment, string? version, CancellationToken ct)
    {
        var state = attachment.Current;
        if (version is not null)
        {
            var entry = NoteHistory.Versions(attachment).FirstOrDefault(v => v.Version == version);
            if (entry is null)
            {
                return ShellResult.Error($"cat: {path.Value} has no version {version} ('history {SectionLookup.Quote(path)}' lists them)");
            }

            state = entry.State;
        }

        // No version block: a text tool can't write an attachment, so there is nothing to pass it to.
        if (pixels is null || !AttachmentText.IsPdf(state)) return ShellResult.Ok(AttachmentText.Line(path.Name, state));

        // Only a stored analysis: cat is the cheap look, running one is for pdf.
        try
        {
            return ShellResult.Ok(AttachmentText.PdfLine(path.Name, state, await pixels.AnalysisAsync(path, state, runIfPending: false, ct), null));
        }
        catch (PixelsUnavailableException e)
        {
            return ShellResult.Ok(AttachmentText.PdfLine(path.Name, state, null, e.Message));
        }
    }

    /// <summary>
    /// For a whole note too long for one call, read by a <c>cat</c> that stands alone: its outline instead
    /// of a cut-off beginning, which would spend the call on text the agent may not need. In a pipeline
    /// the full note flows on; a section that is too long is cut as usual, since it was asked for.
    /// </summary>
    public static ShellResult OutlineIfTooLong(string arg, ShellResult result)
    {
        if (result.IsError || result.Output.Length <= OutputLimit.MaxChars) return result;
        var (pathText, fragment) = SectionAddress.Split(arg);
        if (fragment is not null || !VaultPath.TryParse(pathText, out var path)) return result;

        var tokens = (result.Output.Length + OutputLimit.CharsPerToken - 1) / OutputLimit.CharsPerToken;
        var quoted = SectionLookup.Quote(path);
        var section = $"\"{path.Value}#Section\"";
        var footer =
            $"\n[note too long for one call (~{Thousands(tokens)} tokens); its outline is above. Read a part: cat {section}, head -n 40 {quoted}, rg -n … {quoted}]";
        // A huge outline is cut on its own, so this footer always survives the shell's output limit.
        var outline = OutputLimit.Apply(ShellResult.Ok(OutlineCommand.Render(path, result.Output)), OutputLimit.MaxChars - footer.Length - 300);
        return result with { Output = outline.Output + footer };
    }

    private static string Thousands(int tokens) => tokens >= 1000 ? $"{tokens / 1000}k" : tokens.ToString();
}
