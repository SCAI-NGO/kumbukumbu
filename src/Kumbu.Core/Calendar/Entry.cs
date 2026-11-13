// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Calendar;

/// <summary>Dated: an appointment or a note given a day. Task: a note with <c>task</c>. Card: a board card with <c>due</c>.</summary>
public enum EntryKind { Dated, Task, Card }

public enum TaskMark { None, Open, Closed }

/// <summary>A note with a time (spec §2).</summary>
/// <param name="ByName">A day or week note by its name, without <c>when</c>.</param>
/// <param name="Zone">The note's <c>tz</c>; null: the viewer's zone.</param>
/// <param name="Column">A card's column, as the board names it.</param>
public sealed record Entry(VaultPath Path, string Title, EntryKind Kind, CalendarTime When, CalendarTime? Until, TaskMark Task,
    DateOnly? Closed, string? Reason, Upcoming? Upcoming, TimeZoneInfo? Zone, bool ByName, string? Column)
{
    public string Vault => Path.Segments[0];

    /// <summary>Whether the entry covers more than one day: a week, or an <c>until</c> on another day than <c>when</c>.</summary>
    public bool Spans => When.Kind == TimeKind.Week || (Until is { } until && until.LastDay != When.FirstDay);
}

/// <summary>A note whose calendar keys can't be read (spec §2.7).</summary>
/// <param name="What">For agents: "when: tomorrow is not a date; write 2026-11-20, 2026-11-20T14:00 or 2026-W47".</param>
/// <param name="Plain">For the web: "`when: tomorrow` – I need a date like 2026-11-20".</param>
/// <param name="Readable">The calendar keys that could be read, as written (for Fix…).</param>
public sealed record CalendarProblem(VaultPath Path, string Title, string What, string Plain, IReadOnlyDictionary<string, string> Readable);
