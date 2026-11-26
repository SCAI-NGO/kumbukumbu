// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <summary>The one text file a reader of a format is given (spec 2026-11-19, decision 2): a file kept as UTF-8 text,
/// whatever its name; a note, a file kept as bytes, an image or a PDF is refused.</summary>
internal static partial class TextFiles
{
    /// <summary>The file's text as the output, or the error <paramref name="command"/> gives; <paramref name="format"/>
    /// names what it reads ("HTML").</summary>
    public static async Task<ShellResult> ReadAsync(string command, string format, INoteReader notes, string arg, CancellationToken ct)
    {
        if (!CommandArgs.TryParsePath(command, arg, out var path, out var pathError)) return pathError!;
        if (await StoreAttachments.GetAsync(notes, path, ct) is { Current: var state })
        {
            if (AttachmentFormat.IsLarge(state.Size)) return ShellResult.Error($"{command}: {path.Value} is {AttachmentFormat.NotRead}");
            if (state.MediaType == AttachmentFormat.OctetStream)
            {
                return ShellResult.Error($"{command}: {path.Value} is kept as bytes (not UTF-8 text); Kumbu² can't read it as {format}");
            }

            if (state.MediaType != AttachmentFormat.TextMediaType)
            {
                return ShellResult.Error($"{command}: {path.Value} is not a text file ('cat {SectionLookup.Quote(path)}' says what it is); Kumbu² reads {format} from text files only");
            }

            return await StoreAttachments.ReadBytesAsync(notes, path, state, ct) is { } bytes
                ? ShellResult.Ok(StoreAttachments.Text(bytes))
                : ShellResult.Error($"{command}: {path.Value}: the file's content is missing");
        }

        var note = await CommandArgs.ReadNoteAsync(command, notes, path, ct);
        return note.IsError ? note : ShellResult.Error($"{command}: {path.Value} is a note; read it with cat");
    }

    /// <summary>
    /// The items joined while they fit the shell's output limit, less <paramref name="reserve"/> for what the caller adds;
    /// then a line saying how many were shown of how many <paramref name="noun"/>, and how to <paramref name="narrow"/> it,
    /// so the shell's own cut never ends inside an item.
    /// </summary>
    public static ShellResult Listed(string command, IReadOnlyList<string> items, string separator, string noun, string narrow, int reserve = 0) =>
        Listed(command, items.Count, items, separator, noun, narrow, reserve);

    /// <summary>As above, for <paramref name="count"/> items made one by one: those past the limit are never made, as
    /// an element's HTML or a node's XML holds all below it, and all of them together can be the file's size squared.</summary>
    public static ShellResult Listed(string command, int count, IEnumerable<string> items, string separator, string noun, string narrow, int reserve = 0)
    {
        // A caller's header past the limit leaves no room, never less than none.
        var budget = Math.Max(0, OutputLimit.MaxChars - 200 - reserve);
        var output = new StringBuilder();
        var shown = 0;
        string? first = null;
        foreach (var item in items)
        {
            first ??= item;
            if (output.Length + separator.Length + item.Length > budget) break;
            if (shown > 0) output.Append(separator);
            output.Append(item);
            shown++;
        }

        if (shown == 0 && first is not null)
        {
            // One item longer than the call: its beginning, said to be cut.
            var cut = first[..Math.Min(first.Length, budget)];
            return ShellResult.Ok(cut + string.Create(CultureInfo.InvariantCulture, $"\n{command}: the first of {count:N0} {noun} is cut at the output limit; {narrow}"));
        }

        if (shown < count)
        {
            output.Append(string.Create(CultureInfo.InvariantCulture, $"\n{command}: {shown:N0} of {count:N0} {noun} shown; {narrow}"));
        }

        return ShellResult.Ok(output.ToString());
    }

    /// <summary>Text on one line: each run of whitespace a space, none at either end.</summary>
    public static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
