// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Accounts;

/// <summary>
/// The ids of deleted users, one per line in <c>&lt;Kumbu:Deletions:Path&gt;/deleted-users</c>: outside MongoDB, in
/// a volume of its own, because a list inside the database would be rolled back by the very restore it must detect. Random
/// ids only, never a name. The file is created readable by the server only, in a directory only it can open, as the
/// recovery keys are, and every addition is flushed to disk. Not configured or not writable, a deletion still goes
/// ahead: the caller learns it wasn't recorded, and the error is logged.
/// </summary>
public sealed class DeletionList(IOptions<KumbuOptions> options, ILogger<DeletionList> log)
{
    public const string FileName = "deleted-users";

    /// <summary>One writer at a time, so concurrent deletions never interleave their lines.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? Dir => string.IsNullOrWhiteSpace(options.Value.Deletions.Path) ? null : options.Value.Deletions.Path;

    public bool Configured => Dir is not null;

    /// <summary>Appends the id, and after it a line per shared vault of the user's that was given a closing period
    /// (collab design §7.3): <c>closing &lt;user id&gt; &lt;vault id&gt; &lt;closingAt, Unix milliseconds&gt;</c>, so that a
    /// repeat after a restore applies the same choice. Ids and a time only. False (and an error logged, never an exception)
    /// when the list isn't configured or can't be written.</summary>
    public async Task<bool> AddAsync(string userId, CancellationToken ct, IReadOnlyDictionary<string, DateTime>? closing = null)
    {
        if (Dir is not { } dir)
        {
            log.LogWarning("Deleted user {UserId} is not in the list of deleted users: Kumbu:Deletions:Path is not set", userId);
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var fileOptions = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(Path.Combine(dir, FileName), fileOptions);
            var text = new System.Text.StringBuilder(userId).Append('\n');
            foreach (var (vaultId, at) in closing ?? new Dictionary<string, DateTime>())
            {
                text.Append(ClosingTag).Append(' ').Append(userId).Append(' ').Append(vaultId).Append(' ')
                    .Append(new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds()).Append('\n');
            }

            // One write: the id and its closing periods arrive together.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text.ToString()), ct);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Any failure, an odd path value included: the deletion has committed, so nothing may throw from here.
            log.LogError("Deleted user {UserId} could not be added to the list of deleted users in {Dir} ({Error})", userId, dir, e.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The ids on the list; empty when it isn't configured, the file doesn't exist yet or can't be read.
    /// Only lines that are a user id (the shape <see cref="UserCatalog"/> makes) count: blank lines, CRLF, stray
    /// whitespace around an id and duplicates are tolerated, anything else is skipped.</summary>
    public async Task<IReadOnlySet<string>> ReadAsync(CancellationToken ct)
    {
        if (Dir is not { } dir) return new HashSet<string>();
        var file = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(file)) return new HashSet<string>();
            var lines = await File.ReadAllLinesAsync(file, ct);
            return lines.Select(l => l.Trim()).Where(IsUserId).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError("The list of deleted users in {Dir} could not be read ({Error})", dir, e.Message);
            return new HashSet<string>();
        }
    }

    private const string ClosingTag = "closing";

    /// <summary>
    /// The closing periods recorded with the deletions (<see cref="AddAsync"/>): user id → (vault id → <c>closingAt</c>).
    /// Lines that aren't of that shape are skipped; a vault recorded twice keeps its earliest time. Empty when the list
    /// isn't configured, doesn't exist yet or can't be read.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>>> ReadClosingAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        if (Dir is not { } dir) return Frozen(result);
        var file = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(file)) return Frozen(result);
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                var parts = line.Trim().Split(' ');
                if (parts.Length != 4 || parts[0] != ClosingTag || !IsUserId(parts[1]) || !IsUserId(parts[2])
                    || !long.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms)
                    || ms > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                {
                    continue;
                }

                var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                var vaults = result.TryGetValue(parts[1], out var v) ? v : result[parts[1]] = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                vaults[parts[2]] = vaults.TryGetValue(parts[2], out var earlier) && earlier < at ? earlier : at;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError("The list of deleted users in {Dir} could not be read ({Error})", dir, e.Message);
        }

        return Frozen(result);

        static IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>> Frozen(Dictionary<string, Dictionary<string, DateTime>> d) =>
            d.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, DateTime>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>32 lowercase hex digits, as <see cref="UserCatalog"/> makes user ids (and the catalog vault ids).</summary>
    internal static bool IsUserId(string s) => s.Length == 32 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
