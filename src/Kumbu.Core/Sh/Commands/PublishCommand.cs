// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// <c>publish ls|add|update|rm</c> (publish spec §3.9), the shell's first command with subcommands. The link is printed
/// once, by <c>add</c>; <c>ls</c> never prints a code or a link. Without a publisher (a shell outside the server) it
/// says so.
/// </summary>
public sealed class PublishCommand(IPublisher? publisher) : ICommand
{
    public string Name => "publish";

    public string Usage => "publish ls [path] | add|update <note> [--depth N] [--without <note>]... | rm <note>";

    public string Summary => "share a note, with the notes it links to, as a read-only copy behind a secret link";

    public string Help => """
        publish ls [path]        the published notes below a path; a link is never shown again
        publish add <note> [--depth N] [--without <note>]... [--dry-run]
                                 publish a copy of the note behind a secret link, shown once
        publish update <note> [--depth N] [--without <note>]...
                                 replace the copy with the notes as they are now; the link stays
          --depth N              also take the notes it links to, N links deep (0 to 5; for add, default 0)
          --without <note>       leave a note out, and what is reached only through it (repeatable)
          --dry-run              add only: list what would be published, publish nothing
          update keeps the stored depth and --without list unless given.
        publish rm <note>        withdraw the publication; its link reads nothing from then on
        Only the vault's owner publishes, with a key or connection she allowed to ("May publish").
        add and update print every note the copy takes and every note left behind. Before you
        publish more than the one note, show that list to the person and let them confirm it.
        Links between the notes of a copy stay links; links to other notes become plain text.
        Frontmatter, tags, comments and images' metadata stay behind. PDFs are not published yet.
        Notes and folders the owner marked unpublishable are never published, nor walked through.
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (publisher is null) return ShellResult.Error("publish: publishing isn't available here");
        if (args.Count == 0 || args[0] is not ("ls" or "add" or "update" or "rm"))
        {
            return ShellResult.Error("publish: give ls, add, update or rm (help publish)") with { Kind = ShellErrorKind.Invalid };
        }

        var sub = args[0];
        var command = $"publish {sub}";
        var (operands, options, optionError) = Options(command, [.. args.Skip(1)], sub);
        if (optionError is not null) return optionError;
        if (operands.Count > 1) return ShellResult.Error($"{command}: too many arguments{CommandArgs.QuoteHint}") with { Kind = ShellErrorKind.Invalid };
        if (operands.Count == 0 && sub != "ls")
        {
            return ShellResult.Error($"{command}: missing operand ({command} <note>)") with { Kind = ShellErrorKind.Invalid };
        }

        if (!CommandArgs.TryParsePath(command, operands.Count == 0 ? "/" : operands[0], out var path, out var pathError))
        {
            return pathError! with { Kind = ShellErrorKind.Invalid };
        }

        try
        {
            return sub switch
            {
                "ls" => List(path, await publisher.ListAsync(path, ct)),
                "add" => Added(path, await publisher.AddAsync(path, options, ct)),
                "update" => Updated(path, await publisher.UpdateAsync(path, options, ct)),
                _ => await WithdrawAsync(publisher, path, ct),
            };
        }
        catch (PublishRefusedException e)
        {
            // Refusals.Of keeps only the command's first word; a subcommand's refusal names the subcommand.
            var error = ShellResult.Error($"{command}: {e.Message}") with { Kind = e.Kind };
            return e.Kind == ShellErrorKind.Refused ? error with { Code = e.Code } : error;
        }
    }

    private static ShellResult List(VaultPath under, IReadOnlyList<PublicationLine> lines)
    {
        if (lines.Count == 0) return ShellResult.Ok("") with { NoMatchHint = $"no published notes below {under}" };
        // A member learns that a note is published, nothing more (publish spec §3.10).
        return ShellResult.Ok(string.Join('\n', lines
            .OrderBy(l => l.Note.Value, TextOrder.Default)
            .Select(l => l.SnapshotAt is { } at
                ? $"{l.Note}  {History.Time(at)}  older: {(l.Older == true ? "yes" : "no")}{(l.Notes is > 1 ? $"  notes: {l.Notes}" : "")}"
                : l.Note.Value)));
    }

    private static ShellResult Added(VaultPath note, PublishOutcome outcome)
    {
        var count = outcome.Notes is { Count: > 1 } notes ? $" with {notes.Count} notes" : "";
        List<string> lines = [outcome.DryRun ? $"would publish {note}{count}; nothing was published" : $"published {note}{count} as a copy of {History.Time(outcome.SnapshotAt)}"];
        if (outcome.Link is { } link) lines.Add($"link (shown once): {link}");
        Bundle(lines, outcome);
        if (Plain(outcome) is { } plain) lines.Add(plain);
        return ShellResult.Ok(string.Join('\n', lines));
    }

    private static ShellResult Updated(VaultPath note, PublishOutcome outcome)
    {
        List<string> lines = [$"updated the published copy of {note} ({History.Time(outcome.SnapshotAt)}); the link stays the same"];
        Bundle(lines, outcome);
        if (outcome.Added is { Count: > 0 } added) lines.AddRange(["added:", .. added.Select(p => "  " + p)]);
        if (outcome.Removed is { Count: > 0 } removed) lines.AddRange(["removed:", .. removed.Select(p => "  " + p)]);
        if (Plain(outcome) is { } plain) lines.Add(plain);
        return ShellResult.Ok(string.Join('\n', lines));
    }

    /// <summary>A bundle's notes and the notes left behind, with why, in words (Publish 2.0 decision 2).</summary>
    private static void Bundle(List<string> lines, PublishOutcome outcome)
    {
        if (outcome.Notes is { Count: > 1 } notes) lines.AddRange(["notes:", .. notes.Select(p => "  " + p)]);
        if (outcome.LeftOut is { Count: > 0 } left) lines.AddRange(["left out:", .. left.Select(l => $"  {l.Path}  {Reason(l.Reason)}")]);
    }

    private static string Reason(string reason) => reason switch
    {
        "unpublishable" => "marked unpublishable",
        "behind" => "only reached through a note that stays out",
        "without" => "taken out by you",
        _ => reason,
    };

    /// <summary>The operands and the options of <paramref name="args"/>, or the error of an option that isn't one of
    /// <c>--depth</c>, <c>--without</c> and <c>--dry-run</c>, isn't the subcommand's, or lacks its value.</summary>
    private static (List<string> Operands, PublishWalk Options, ShellResult? Error) Options(string command, List<string> args, string sub)
    {
        var operands = new List<string>();
        int? depth = null;
        var without = new List<VaultPath>();
        var dry = false;
        (List<string>, PublishWalk, ShellResult?) Fail(string message) =>
            (operands, PublishWalk.None, ShellResult.Error($"{command}: {message}") with { Kind = ShellErrorKind.Invalid });
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                operands.Add(arg);
                continue;
            }

            if (sub is not ("add" or "update") || (arg == "--dry-run" && sub != "add"))
            {
                return Fail($"{arg}: {(sub == "update" ? "update takes --depth and --without" : $"{sub} takes no options")}");
            }

            switch (arg)
            {
                case "--dry-run":
                    dry = true;
                    break;
                case "--depth" when i + 1 < args.Count && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) && d <= 5:
                    depth = d;
                    i++;
                    break;
                case "--depth":
                    return Fail("--depth takes a number from 0 to 5");
                case "--without" when i + 1 < args.Count:
                    if (!CommandArgs.TryParsePath(command, args[i + 1], out var p, out var error)) return (operands, PublishWalk.None, error! with { Kind = ShellErrorKind.Invalid });

                    without.Add(p);
                    i++;
                    break;
                case "--without":
                    return Fail("--without takes a note");
                default:
                    return Fail($"invalid option {arg} (help publish)");
            }
        }

        return (operands, new PublishWalk(depth, without.Count == 0 ? null : without, dry), null);
    }

    private static async Task<ShellResult> WithdrawAsync(IPublisher publisher, VaultPath note, CancellationToken ct)
    {
        await publisher.WithdrawAsync(note, ct);
        return ShellResult.Ok($"withdrew the publication of {note}; its link reads nothing now");
    }

    /// <summary>The embeds that became plain text, joined by commas, and the sentence about PDFs after a semicolon.</summary>
    private static string? Plain(PublishOutcome outcome)
    {
        var names = outcome.Plain.Count > 0 ? string.Join(", ", outcome.Plain) : null;
        var pdfs = outcome.PdfsLeftOut ? PublishOutcome.PdfsNotYet : null;
        return names is null && pdfs is null ? null : "not copied, plain text instead: " + string.Join("; ", new[] { names, pdfs }.OfType<string>());
    }
}
