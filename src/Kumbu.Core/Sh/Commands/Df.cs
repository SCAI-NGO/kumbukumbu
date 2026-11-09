// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// What the caller's own vaults take in the database, against her storage limit (WebDAV design §3.12). A shared vault
/// is never listed, not even as "n/a": it counts against its owner, and its figure is the owner's business.
/// </summary>
public sealed class Df(INoteReader notes, IStorageFigures figures) : ICommand
{
    // -h because agents type "df -h" out of habit; the output is always human-readable.
    private static readonly Option[] Spec = [new('h', "human-readable"), new('b', "bytes")];

    public const string SharedVault = "df shows only vaults you own; a shared vault counts against its owner.";

    public string Name => "df";

    public string Usage => "df [-h] [-b] [/vault]";

    public string Summary => "what your own vaults take, against your storage limit";

    public string Help => """
        df [-h] [-b] [/vault]
          What your own vaults take in the database, and your storage limit. Used is everything a vault
          keeps: its notes with their history, the trash, moves, embeddings, checkpoints and attachments;
          Notes and Attachments count what is there now, not the trash. Writes that add data are refused
          once Used reaches the limit; rm, mv and emptying the trash (in the web app) always work.
          A vault shared with you is not listed: what anyone writes there counts against its owner.
          With a key that opens only some of your vaults, the rows and the total are those vaults';
          your other vaults count against the limit too, so no percentage is shown.
          Sizes are B, KB, MB and GB (1 KB = 1024 B); -h is accepted and changes nothing.
          -b, --bytes   exact bytes

          df
          df /Work
          du -s /Work      what /Work holds now, without history and trash (help du)
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        if (options.Operands.Count > 1) return ShellResult.Error("df: too many arguments" + CommandArgs.QuoteHint);

        var bytes = options.Has("bytes");
        var own = await figures.OwnedAsync(ct);
        var owned = own.Vaults.OrderBy(f => f.Vault, TextOrder.Default).ToList();
        if (options.Operands.Count == 1)
        {
            if (!CommandArgs.TryParsePath(Name, options.Operands[0], out var path, out var pathError)) return pathError!;
            if (!path.IsRoot)
            {
                if (path.Segments.Count > 1) return ShellResult.Error($"df: {path.Value}: df shows whole vaults; du sizes a folder");
                var vault = path.Segments[0];
                var one = owned.FirstOrDefault(f => f.Vault == vault);
                if (one is not null) return ShellResult.Ok(string.Join('\n', Table([one], one.Used, bytes, out _)));
                return await notes.VaultInfoAsync(vault, ct) is { Owner: not null }
                    ? ShellResult.Error($"df: {path.Value}: {SharedVault}")
                    : ShellResult.Error($"df: {path.Value}: No such vault");
            }
        }

        if (owned.Count == 0) return ShellResult.Ok("(no vaults of your own)");
        var used = own.Total ?? owned.Sum(f => f.Used);
        var lines = Table(owned, used, bytes, out var columns);
        lines.Add(new string('─', columns.Width + 2));
        var total = "Total".PadRight(columns.Name) + AttachmentText.Size(used, bytes).PadLeft(columns.Used);
        lines.Add(own.Limit switch
        {
            null => $"{total} (no limit)",
            { } max when own.AllOwned => $"{total}  of {AttachmentText.Size(max, bytes)} ({Percent(used, max)} %)",
            // The rows leave out vaults the limit counts too: a percentage of it would promise room the quota doesn't give.
            { } max => $"{total}  (vaults this key opens; your other vaults count against the {AttachmentText.Size(max, bytes)} limit too)",
        });
        return ShellResult.Ok(string.Join('\n', lines));
    }

    private readonly record struct Columns(int Name, int Used, int Notes, int Attachments)
    {
        public int Width => Name + Used + 1 + Notes + 2 + Attachments;
    }

    /// <summary>The header and a line per vault; the name column leaves four spaces after the longest name, Used is as
    /// wide as the widest figure, the total <paramref name="used"/> included, the counts are right-aligned under their headings.</summary>
    private static List<string> Table(IReadOnlyList<VaultFigure> owned, long used, bool bytes, out Columns columns)
    {
        var total = AttachmentText.Size(used, bytes);
        columns = new Columns(
            Math.Max("Vault".Length, owned.Max(f => f.Vault.Length + 1)) + 4,
            Math.Max("Used".Length, Math.Max(total.Length, owned.Max(f => AttachmentText.Size(f.Used, bytes).Length))),
            Math.Max("Notes".Length, owned.Max(f => Count(f.Notes).Length)),
            Math.Max("Attachments".Length, owned.Max(f => Count(f.Attachments).Length)));
        var c = columns;
        List<string> lines = ["Vault".PadRight(c.Name) + "Used".PadRight(c.Used) + " " + "Notes".PadLeft(c.Notes) + "  " + "Attachments".PadLeft(c.Attachments)];
        lines.AddRange(owned.Select(f =>
            ("/" + f.Vault).PadRight(c.Name) + AttachmentText.Size(f.Used, bytes).PadLeft(c.Used) + " " + Count(f.Notes).PadLeft(c.Notes) + "  " + Count(f.Attachments).PadLeft(c.Attachments)));
        return lines;
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whole per cent; a limit of 0 (every write refused) is full.</summary>
    private static string Percent(long used, long limit) =>
        (limit <= 0 ? 100 : Math.Round(used * 100.0 / limit, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
}
