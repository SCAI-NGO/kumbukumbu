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

    public string Usage => "publish ls|add|update|rm <note>";

    public string Summary => "share one note as a read-only copy behind a secret link";

    public string Help => """
        publish ls [path]        the published notes below a path; a link is never shown again
        publish add <note>       publish a copy of one note behind a secret link, shown once
        publish update <note>    replace the copy with the note as it is now; the link stays
        publish rm <note>        withdraw the publication; its link reads nothing from then on
        Only the vault's owner publishes, with a key or connection she allowed to ("May publish").
        Links to other notes become plain text; frontmatter, tags, comments and images' metadata
        stay behind. PDFs are not published yet. Notes and folders the owner marked unpublishable
        can't be published at all.
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
        var operands = args.Skip(1).ToList();
        if (CommandArgs.CheckOptions(command, operands) is { } optionError) return optionError with { Kind = ShellErrorKind.Invalid };
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
                "add" => Added(path, await publisher.AddAsync(path, ct)),
                "update" => Updated(path, await publisher.UpdateAsync(path, ct)),
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
            .Select(l => l.SnapshotAt is { } at ? $"{l.Note}  {History.Time(at)}  older: {(l.Older == true ? "yes" : "no")}" : l.Note.Value)));
    }

    private static ShellResult Added(VaultPath note, PublishOutcome outcome)
    {
        List<string> lines = [$"published {note} as a copy of {History.Time(outcome.SnapshotAt)}"];
        if (outcome.Link is { } link) lines.Add($"link (shown once): {link}");
        if (Plain(outcome) is { } plain) lines.Add(plain);
        return ShellResult.Ok(string.Join('\n', lines));
    }

    private static ShellResult Updated(VaultPath note, PublishOutcome outcome)
    {
        var line = $"updated the published copy of {note} ({History.Time(outcome.SnapshotAt)}); the link stays the same";
        return ShellResult.Ok(Plain(outcome) is { } plain ? line + "\n" + plain : line);
    }

    private static async Task<ShellResult> WithdrawAsync(IPublisher publisher, VaultPath note, CancellationToken ct)
    {
        await publisher.WithdrawAsync(note, ct);
        return ShellResult.Ok($"withdrew the publication of {note}; its link reads nothing now");
    }

    /// <summary>The embeds that became plain text, joined by commas, and the sentence about PDFs after a semicolon.</summary>
    private static string? Plain(PublishOutcome outcome)
    {
        var names = outcome.Plain.Where(p => p != PublishOutcome.PdfsNotYet).ToList();
        string[] parts = [.. names.Count > 0 ? [string.Join(", ", names)] : Array.Empty<string>(), .. outcome.Plain.Contains(PublishOutcome.PdfsNotYet) ? [PublishOutcome.PdfsNotYet] : Array.Empty<string>()];
        return parts.Length == 0 ? null : "not copied, plain text instead: " + string.Join("; ", parts);
    }
}
