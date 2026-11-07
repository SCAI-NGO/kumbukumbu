// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// The size of the current content below a path (WebDAV design §3.12): notes' text, attachments' bytes, in any vault the
/// caller can read, shared ones included; it shows only sizes of what she may read anyway. Computed from what the store
/// lists, so history, trash and embeddings, which <c>df</c> counts, are not in it.
/// </summary>
public sealed class Du(INoteReader notes) : ICommand
{
    // -h because agents type "du -sh" out of habit; the output is always human-readable.
    private static readonly Option[] Spec =
    [
        new('h', "human-readable"), new('b', "bytes"), new('s', "summarize"), new('d', "max-depth", TakesValue: true),
    ];

    public string Name => "du";

    public string Usage => "du [-s] [-d N] [path]";

    public string Summary => "sizes of what is below a path, largest first";

    public string Help => """
        du [-h] [-b] [-s] [-d N] [path]
          The size of what is below a path now: notes' text (UTF-8 bytes) and attachments' bytes, in
          any vault you can read, shared ones included. Without -s, the path's direct children, folders
          summed (they end with '/'), largest first, then a total line; no path sizes the vaults.
          du of a whole vault is smaller than its line in df: df counts everything the vault keeps in
          the database (history, trash, moves, embeddings, checkpoints), du only what is there now.
          Sizes are B, KB, MB and GB (1 KB = 1024 B); -h is accepted and changes nothing.
          -s, --summarize     the total only
          -d, --max-depth N   N levels deep instead of one (-d 0 is -s)
          -b, --bytes         exact bytes
          There is no globbing: du lists a folder's children itself.

          du /Work
          du -sh /Work/projects
          du -d 2 /Work | head -20
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "du sizes what is there now") is { } versionError) return versionError;
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        if (options.Operands.FirstOrDefault(o => o.Contains('*')) is { } glob)
        {
            var star = glob.IndexOf('*');
            var folder = glob.LastIndexOf('/', star) is var slash and > 0 ? glob[..slash] : "/";
            return ShellResult.Error($"du: there is no globbing ('|' is the only operator, '*' an ordinary character); du lists a folder's children itself: du {folder}");
        }

        if (options.Operands.Count > 1) return ShellResult.Error("du: too many arguments" + CommandArgs.QuoteHint);

        var depth = 1;
        if (options.Value("max-depth") is { } d && (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out depth)))
        {
            return ShellResult.Error($"du: invalid maximum depth '{d}'");
        }

        if (options.Has("summarize"))
        {
            if (options.Has("max-depth") && depth > 0) return ShellResult.Error("du: -s and -d N don't go together; -s is -d 0");
            depth = 0;
        }

        var path = VaultPath.Root;
        if (options.Operands.Count == 1 && !CommandArgs.TryParsePath(Name, options.Operands[0], out path, out var pathError)) return pathError!;

        var bytes = options.Has("bytes");
        if (!path.IsRoot && await notes.ReadAsync(path, ct) is { } content) return ShellResult.Ok($"{Df.Size(Encoding.UTF8.GetByteCount(content), bytes)}  {path.Value}");
        if (!path.IsRoot && await StoreAttachments.GetAsync(notes, path, ct) is { } file) return ShellResult.Ok($"{Df.Size(file.Current.Size, bytes)}  {path.Value}");

        // What lies below, by its segments: a note or an attachment is a file; a folder's marker and an empty vault stand
        // for the folder itself, which then shows with nothing in it.
        var items = new List<(IReadOnlyList<string> Segments, long Size, bool Folder)>();
        foreach (var note in await notes.ReadNotesUnderAsync(path, ct)) items.Add((note.Path.Segments, Encoding.UTF8.GetByteCount(note.Content), false));
        foreach (var attachment in await StoreAttachments.UnderAsync(notes, path, ct))
        {
            items.Add(FolderMarker.Is(attachment.Path)
                ? (attachment.Path.Segments.Take(attachment.Path.Segments.Count - 1).ToList(), attachment.Current.Size, true)
                : (attachment.Path.Segments, attachment.Current.Size, false));
        }

        var vaults = await notes.ListVaultsAsync(ct);
        if (path.IsRoot) items.AddRange(vaults.Select(v => ((IReadOnlyList<string>)[v], 0L, true)));
        if (items.Count == 0 && !path.IsRoot && !(path.Segments.Count == 1 && vaults.Contains(path.Segments[0])))
        {
            return ShellResult.Error($"du: cannot access '{path.Value}': No such file or directory");
        }

        var total = items.Sum(i => i.Size);
        if (depth == 0) return ShellResult.Ok($"{Df.Size(total, bytes)}  {path.Value}");

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var top = path.Segments.Count;
        foreach (var (segments, size, folder) in items)
        {
            var below = segments.Count - top;
            for (var level = 1; level <= Math.Min(depth, below); level++)
            {
                var entry = "/" + string.Join('/', segments.Take(top + level)) + (folder || level < below ? "/" : "");
                sizes[entry] = sizes.GetValueOrDefault(entry) + size;
            }
        }

        var rows = sizes.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).Select(e => (Size: Df.Size(e.Value, bytes), Name: e.Key)).ToList();
        rows.Add((Df.Size(total, bytes), "total"));
        var width = rows.Max(r => r.Size.Length);
        return ShellResult.Ok(string.Join('\n', rows.Select(r => r.Size.PadLeft(width) + "  " + r.Name)));
    }
}
