// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class Cat(INoteReader notes) : ICommand
{
    public string Name => "cat";

    public string Usage => "cat <path>[#section]";

    public string Summary => "print a note, or one section of it";

    public string Help => """
        cat <path>[#section]
          Print a note, or one section of it: the heading and everything below it up to the next
          heading of the same or a higher level. 'outline <path>' lists the sections.
          Address nested headings as #Parent#Child; a unique heading works on its own. Duplicate
          headings under one parent take an index, #Notes[2]. Write a literal '#' as \#.
          The version token comes separately, always for the whole note; pass it to 'edit' or 'write'.
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
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("cat: missing operand");
        if (args.Count > 1) return ShellResult.Error($"cat: too many arguments{CommandArgs.QuoteHint}");

        return await CommandArgs.ReadAddressAsync(Name, notes, args[0], ct);
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
