// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class History(INoteStore notes) : ICommand
{
    public string Name => "history";

    public string Usage => "history <path>";

    public string Summary => "versions of a note, newest first";

    public string Help => $"""
        history <path>
          List the versions of a note, newest first: version, time written (UTC), writer, lines.
          The first line is the current version. Up to {NoteWriter.HistoryLimit} earlier versions are kept; writes by the
          same writer within {NoteWriter.CoalesceWindow.TotalMinutes} minutes count as one, so the state before a session survives.
          'restore <path> <version>' makes an earlier version current again.

          history /Demo/welcome.md
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("history: missing operand");
        if (args.Count > 1) return ShellResult.Error("history: too many arguments");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        var note = await notes.GetAsync(path, ct);
        if (note is null) return await CommandArgs.ReadNoteAsync(Name, notes, path, ct);

        var lines = new List<string> { Line(note.Content, note.LastWriter, note.LastWriteAt) + "  (current)" };
        lines.AddRange(note.History.Select(h => Line(h.Content, h.WrittenBy, h.WrittenAt)));
        return ShellResult.Ok(Lines.Join(lines)) with { Version = new NoteVersion(path.Value, note.Version) };
    }

    private static string Line(string content, string writer, DateTime at) =>
        $"{VersionToken.Of(content)}  {Time(at)}  {writer}  {Lines.Count(content)}";

    internal static string Time(DateTime at) => at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
