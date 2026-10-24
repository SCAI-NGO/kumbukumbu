// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;

namespace Kumbu.Core.Notes;

/// <summary>
/// Previews made in this process, by token, for an hour (a <c>replace</c>, a <c>restore</c> of a folder).
/// A plan is applied once. What became of it is remembered for the same hour, so a client that lost the
/// answer to an apply learns that it was applied, instead of "unknown". Not kept across a restart.
/// </summary>
public sealed class Plans
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (object Plan, DateTime Created)> _open = new();
    private readonly ConcurrentDictionary<string, (string Verb, string Detail, DateTime At)> _done = new();

    public void Add(string token, object plan, DateTime now)
    {
        foreach (var (key, old) in _open)
        {
            if (now - old.Created > Lifetime) _open.TryRemove(key, out _);
        }

        foreach (var (key, old) in _done)
        {
            if (now - old.At > Lifetime) _done.TryRemove(key, out _);
        }

        _done.TryRemove(token, out _);
        _open[token] = (plan, now);
    }

    /// <summary>Takes the plan out, so a second apply can't run it again; null if unknown or expired.</summary>
    public T? Take<T>(string token, DateTime now) where T : class =>
        _open.TryRemove(token, out var entry) && now - entry.Created <= Lifetime ? entry.Plan as T : null;

    /// <summary>Puts a plan back that was taken for a call it doesn't belong to.</summary>
    public void Return(string token, object plan, DateTime created) => _open.TryAdd(token, (plan, created));

    /// <summary>Records that a plan was applied, with what it did ("replaced 5 occurrences in 2 notes").</summary>
    public void Applied(string token, string result, DateTime now) => _done[token] = ("was already applied", $" ({result}); a plan is applied once", now);

    /// <summary>Records that applying a plan failed, and why; it can't be tried again.</summary>
    public void Failed(string token, string reason, DateTime now) => _done[token] = ("failed", $": {reason}; preview again", now);

    /// <summary>Why a token can't be applied: already applied or failed (and when), or unknown.</summary>
    public string Unavailable(string tool, string token, DateTime now) =>
        _done.TryGetValue(token, out var done) && now - done.At <= Lifetime
            ? $"{tool}: plan {token} {done.Verb} at {done.At.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC{done.Detail}"
            : $"{tool}: unknown or expired plan {token} (a plan lasts an hour, and not across a server restart); preview again";
}
