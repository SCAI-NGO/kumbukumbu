// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Calendar;

/// <summary>
/// The one place that decides whether a note is a calendar entry (spec §2): by <c>when</c>, by its name as a day or week note,
/// or as a board card with <c>due</c>. A value it can't read makes a problem, never a silently dropped entry. Pure; changes nothing.
/// </summary>
public static partial class EntryReader
{
    // Keyed by the content string, as TagReader's: an entry lives as long as that version of the note is held.
    private static readonly ConditionalWeakTable<string, FrontmatterValues> Cache = new();

    private const string DateHint = "write 2026-11-20, 2026-11-20T14:00 or 2026-W47";

    /// <summary>The parsed frontmatter of <paramref name="content"/>, remembered for as long as that text is held.</summary>
    internal static FrontmatterValues Parsed(string content) => Cache.GetValue(content, Frontmatter.Read);

    /// <summary>Exactly one of Entry and Problem is set, or neither (not an entry). A card's task key gives both: the entry, and the problem.</summary>
    public static (Entry? Entry, CalendarProblem? Problem) Read(Note note, BoardDefinition? board)
    {
        var path = note.Path;
        if (ArchiveFolder.Contains(path)) return (null, null);

        var title = path.Name.EndsWith(".md", StringComparison.Ordinal) ? path.Name[..^".md".Length] : path.Name;
        var named = IsDayOrWeekName(path.Name, out var byName);
        var cardPlace = board is not null && IsCardPlace(path);

        var read = Parsed(note.Content);
        if (read.Problem is not null)
        {
            return named || HasCalendarKeyLine(note.Content, cardPlace)
                ? (null, new CalendarProblem(path, title, $"the frontmatter does not parse: {read.Problem}",
                    "I can't read this note's properties; fix them in the note", new Dictionary<string, string>()))
                : (null, null);
        }

        var values = read.Values;
        if (cardPlace && values.GetValueOrDefault("column") is string { Length: > 0 } column)
            return ReadCard(path, title, values, board!, column.Trim());
        // A card of a board that can't be read: its due would otherwise be nobody's (anti-goal 1).
        if (board is null && IsCardPlace(path) && Given(values, "column") is not null && Given(values, "due") is { } due)
        {
            return (null, new CalendarProblem(path, title, $"due: {Text(due)} is on a card of a board that can't be read (Board.md); fix the board",
                "This card's date can't be shown until its board can be read again", new Dictionary<string, string>()));
        }


        var problems = new Problems(path, title);
        var whenValue = Given(values, "when");
        var untilValue = Given(values, "until");
        CalendarTime? when = null;
        if (whenValue is not null)
        {
            if (CalendarTime.TryParse(whenValue, out var parsed))
            {
                problems.Readable("when", whenValue);
                when = parsed;
            }
            else
            {
                problems.Add($"when: {Text(whenValue)} is not a date; {DateHint}", $"`when: {Text(whenValue)}` – I need a date like 2026-11-20");
            }
        }
        else if (named)
        {
            when = byName;
        }
        else if (untilValue is not null)
        {
            problems.Add("until without when; give the start as when too", "This has an end but no start – I need the start too");
        }
        else
        {
            return (null, null);
        }

        var zone = ReadZone(values, problems);

        CalendarTime? until = null;
        if (untilValue is not null)
        {
            if (!CalendarTime.TryParse(untilValue, out var end))
            {
                problems.Add($"until: {Text(untilValue)} is not a date; {DateHint}", $"`until: {Text(untilValue)}` – I need a date like 2026-11-20");
            }
            else if (when is { } start && end.Kind != start.Kind)
            {
                problems.Add($"until is a {KindName(end.Kind)} but when is a {KindName(start.Kind)}; give both the same kind",
                    $"`until` is a {KindName(end.Kind)} but `when` is a {KindName(start.Kind)} – I need both of the same kind");
            }
            else if (when is { } from && Before(end, from, zone ?? TimeZoneInfo.Utc))
            {
                problems.Add($"until {Text(untilValue)} is before when {(whenValue is null ? from.Write() : Text(whenValue))}",
                    $"The end, {Human(end)}, is before the start, {Human(from)}");
            }
            else
            {
                problems.Readable("until", untilValue);
                until = end;
            }
        }

        var task = TaskMark.None;
        if (Given(values, "task") is { } taskValue)
        {
            var status = taskValue is string text ? text.Trim().ToLowerInvariant() : null;
            if (status is "open" or "closed")
            {
                problems.Readable("task", taskValue);
                task = status == "open" ? TaskMark.Open : TaskMark.Closed;
            }
            else
            {
                problems.Add($"task: {Text(taskValue)} is neither open nor closed", $"`task: {Text(taskValue)}` – I need open or closed");
            }
        }

        DateOnly? closed = null;
        if (Given(values, "closed") is { } closedValue)
        {
            if (CalendarTime.TryParse(closedValue, out var day) && day.Kind == TimeKind.Day)
            {
                problems.Readable("closed", closedValue);
                closed = day.Date;
            }
            else
            {
                problems.Add($"closed: {Text(closedValue)} is not a day; write 2026-11-20", $"`closed: {Text(closedValue)}` – I need a day like 2026-11-20");
            }
        }

        var reason = ReadReason(values, problems);
        var upcoming = ReadUpcoming(values, problems);
        if (problems.Any) return (null, problems.Problem());

        return (new Entry(path, title, task == TaskMark.None ? EntryKind.Dated : EntryKind.Task, when!, until, task, closed, reason,
            upcoming, zone, named && whenValue is null, null), null);
    }

    /// <summary>Whether <paramref name="name"/> is exactly <c>YYYY-MM-DD.md</c> or <c>YYYY-Www.md</c> of a real day or week.</summary>
    public static bool IsDayOrWeekName(string name, out CalendarTime when)
    {
        when = default!;
        return DayOrWeekName().IsMatch(name)
            && CalendarTime.TryParse(name[..^".md".Length], out when)
            && when.Kind is TimeKind.Day or TimeKind.Week;
    }

    // The board decides by place (spec §4): directly in /<vault>/Board/, not Board.md itself.
    private static bool IsCardPlace(VaultPath path) =>
        path.Segments.Count == 3 && BoardPaths.Folder(path.Segments[0]).Child(path.Name) == path && path != BoardPaths.Definition(path.Segments[0]);

    /// <summary>A card's <c>due</c> is its <c>when</c>; its column is its status (spec §2.5).</summary>
    private static (Entry? Entry, CalendarProblem? Problem) ReadCard(
        VaultPath path, string title, IReadOnlyDictionary<string, object?> values, BoardDefinition board, string written)
    {
        var problems = new Problems(path, title);
        var marked = Given(values, "task") is not null || Given(values, "closed") is not null;
        if (Given(values, "due") is not { } dueValue)
        {
            // A card's date is its due; a when or a task on it would otherwise vanish without a word (anti-goal 1).
            if (Given(values, "when") is null && !marked) return (null, null);
            problems.Add("on a card the date is its due; set it with the board tool", "On a card the date goes in `due`; set it on the board");
            return (null, problems.Problem());
        }

        CalendarTime? due = null;
        if (CalendarTime.TryParse(dueValue, out var day) && day.Kind == TimeKind.Day)
        {
            problems.Readable("due", dueValue);
            due = day;
        }
        else
        {
            problems.Add($"due: {Text(dueValue)} is not a date; write 2026-11-20", $"`due: {Text(dueValue)}` – I need a date like 2026-11-20");
        }

        var reason = ReadReason(values, problems);
        var upcoming = ReadUpcoming(values, problems);
        if (marked) problems.Add(CardMarked, CardMarkedPlain);
        if (due is null || upcoming is null && Given(values, "upcoming") is not null) return (null, problems.Problem());

        // A column the board doesn't have is the board's problem; for the calendar the deadline is still open.
        var column = board.Find(written);
        var closed = column is { Archivable: true } or { NeedsReason: true };
        var entry = new Entry(path, title, EntryKind.Card, due!, null, closed ? TaskMark.Closed : TaskMark.Open, null,
            closed ? reason : null, upcoming, null, false, column?.Name ?? written);

        return (entry, problems.Any ? problems.Problem() : null);
    }

    private const string CardMarked = "on a card the column is the status; move it with the board tool";
    private const string CardMarkedPlain = "Whether this card is done is its column on the board; change it there";

    private static TimeZoneInfo? ReadZone(IReadOnlyDictionary<string, object?> values, Problems problems)
    {
        if (Given(values, "tz") is not { } value) return null;
        if (Zones.Find(value as string) is { } zone)
        {
            problems.Readable("tz", value);
            return zone;
        }

        problems.Add($"tz: {Text(value)} is not a time zone (an IANA name such as Europe/Vienna)",
            $"`tz: {Text(value)}` – I need a time zone like Europe/Vienna");
        return null;
    }

    private static string? ReadReason(IReadOnlyDictionary<string, object?> values, Problems problems)
    {
        if (Given(values, "reason") is not string reason) return null;
        problems.Readable("reason", reason);
        return reason;
    }

    private static Upcoming? ReadUpcoming(IReadOnlyDictionary<string, object?> values, Problems problems)
    {
        if (Given(values, "upcoming") is not { } value) return null;
        if (value is string text && Upcoming.TryParse(text, out var upcoming))
        {
            problems.Readable("upcoming", value);
            return upcoming;
        }

        problems.Add($"upcoming: {Text(value)} is not a span; write 7d, 2w or 12h", $"`upcoming: {Text(value)}` – I need a span like 7d");
        return null;
    }

    // An empty key ("when:", as templates leave it) is no value.
    private static object? Given(IReadOnlyDictionary<string, object?> values, string key) =>
        values.GetValueOrDefault(key) switch
        {
            string text when string.IsNullOrWhiteSpace(text) => null,
            var value => value,
        };

    private const int ShownLength = 60;

    /// <summary>A value as the person wrote it, on one line and cut short: it is shown back, not stored.</summary>
    private static string Text(object? value)
    {
        var text = value switch
        {
            string s => s,
            IReadOnlyList<object?> list => "[" + string.Join(", ", list.Take(10).Select(Text)) + (list.Count > 10 ? ", …" : "") + "]",
            IReadOnlyDictionary<string, object?> => "{…}",
            _ => value?.ToString() ?? "",
        };
        text = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return text.Length > ShownLength ? text[..ShownLength].TrimEnd() + "…" : text;
    }

    private static string KindName(TimeKind kind) => kind switch
    {
        TimeKind.Day => "day",
        TimeKind.Time => "time",
        _ => "week",
    };

    private static string Human(CalendarTime value) => value.Kind switch
    {
        TimeKind.Day => value.Date.ToString("d MMM", CultureInfo.InvariantCulture),
        TimeKind.Time => value.Date.ToString("d MMM", CultureInfo.InvariantCulture) + " " + value.Clock.ToString("HH:mm", CultureInfo.InvariantCulture),
        _ => $"Week {value.IsoWeek}",
    };

    // Two local times compare as written; once an offset is involved, as instants (in the note's zone, else UTC).
    private static bool Before(CalendarTime until, CalendarTime when, TimeZoneInfo zone) =>
        until.Kind != TimeKind.Time ? until.Date < when.Date
        : until.Offset is null && when.Offset is null ? until.Date.ToDateTime(until.Clock) < when.Date.ToDateTime(when.Clock)
        : until.StartUtc(zone) < when.StartUtc(zone);

    /// <summary>Whether a frontmatter block that doesn't parse holds a calendar key, so that its note is a problem rather than nothing.</summary>
    private static bool HasCalendarKeyLine(string content, bool cardPlace)
    {
        var lines = Lines.Split(content);
        var end = Outline.FrontmatterEnd(lines);
        for (var i = 1; i < end - 1; i++)
        {
            if (CalendarKeyLine().Match(lines[i]) is { Success: true } match && (match.Groups["key"].Value != "due" || cardPlace))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(?<q>[""']?)(?<key>when|until|task|due)\k<q>[ \t]*:")]
    private static partial Regex CalendarKeyLine();

    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{4}-W[0-9]{2})\.md\z")]
    private static partial Regex DayOrWeekName();

    /// <summary>What is wrong with a note's calendar keys, and the keys that read.</summary>
    private sealed class Problems(VaultPath path, string title)
    {
        private readonly List<(string What, string Plain)> found = [];
        private readonly Dictionary<string, string> readable = [];

        public bool Any => found.Count > 0;

        public void Add(string what, string plain) => found.Add((what, plain));

        public void Readable(string key, object value)
        {
            if (value is string written) readable[key] = written;
        }

        public CalendarProblem Problem() =>
            new(path, title, string.Join(". ", found.Select(f => f.What)), string.Join(". ", found.Select(f => f.Plain)), readable);
    }
}
