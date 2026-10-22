// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

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

          cat /Demo/welcome.md
          cat "/Demo/welcome.md#Try it"
          cat "/Work/plan.md#Architecture#Gates" | head -n 20
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("cat: missing operand");
        if (args.Count > 1) return ShellResult.Error("cat: too many arguments");

        return await CommandArgs.ReadAddressAsync(Name, notes, args[0], ct);
    }
}
