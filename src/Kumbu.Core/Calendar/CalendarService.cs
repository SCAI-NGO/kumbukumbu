// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Calendar;

/// <summary>What <see cref="CalendarService.SetAsync"/> changes: each given key is set, or (given as null or empty) removed;
/// <see cref="Task"/> true sets <c>task: open</c> unless the task is closed, false removes <c>task</c>, <c>closed</c> and <c>reason</c>.</summary>
public sealed record CalendarChange(Optional<string?> When = default, Optional<string?> Until = default, bool? Task = null,
    Optional<string?> Upcoming = default, Optional<string?> Tz = default, Optional<string?> Reason = default);

/// <summary>Later's +1 day and +1 week (spec §7.13).</summary>
public enum LaterStep { Day, Week }

/// <summary>Postpone moves a task, start and end together; More time moves only its end (spec §7.13).</summary>
public enum LaterPart { Postpone, MoreTime }

/// <summary>
/// The calendar's writes, for <c>sh calendar</c> and the REST API alike (spec §6.2, §7.5, §7.13). A change reads the note, changes
/// only the calendar's keys through <see cref="Frontmatter.Set"/> (every other byte stays) and writes it back with
/// compare-and-swap as the caller, retrying on the new state. A board card's status and date are the board's: only Later
/// changes a card's <c>due</c>, through <see cref="BoardService"/>.
/// </summary>
public sealed class CalendarService(INoteStore store, NoteWriter writer, BoardService board, Func<DateTime> now, ICallerZone zone)
{
    private const string Tool = "calendar";
    private const int Attempts = 3;
    private const int MaxReason = 2000;
    private const int ShownReason = 80;
    private const string DateHint = "give 2026-11-20, 2026-11-20T14:00 or 2026-W47";

    /// <summary>The calendar's keys, in the order new ones are written (spec §2.2).</summary>
    private static readonly string[] Keys = ["when", "until", "task", "closed", "reason", "upcoming", "tz"];

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private ICaller Caller => writer.Caller;

    public async Task<ShellResult> SetAsync(string path, CalendarChange change, CancellationToken ct)
    {
        if (!change.When.IsGiven && !change.Until.IsGiven && change.Task is null && !change.Upcoming.IsGiven && !change.Tz.IsGiven && !change.Reason.IsGiven)
            return Error("set needs at least one of when, until, task, upcoming, tz or reason");

        CalendarTime? when = null;
        if (change.When.IsGiven)
        {
            if (string.IsNullOrWhiteSpace(change.When.Value)) return Error("--when needs a value; calendar unset <note> takes a note out of the calendar");
            if (!CalendarTime.TryParse(change.When.Value.Trim(), out var parsed)) return Error($"--when {change.When.Value} is not a date; {DateHint}");
            when = parsed;
        }

        CalendarTime? until = null;
        if (Given(change.Until) is { } untilText && !CalendarTime.TryParse(untilText, out until)) return Error($"--until {untilText} is not a date; {DateHint}");

        Upcoming? upcoming = null;
        if (Given(change.Upcoming) is { } upcomingText && !Upcoming.TryParse(upcomingText, out upcoming))
            return Error($"--upcoming {upcomingText} is not a span; give 7d, 2w or 12h");

        TimeZoneInfo? tz = null;
        var tzText = Given(change.Tz);
        if (tzText is not null && (tz = Zones.Find(tzText)) is null) return Error($"--tz {tzText} is not a time zone; give an IANA name such as Europe/Vienna");

        var reason = Given(change.Reason);
        if (reason is { Length: > MaxReason }) return Error($"a reason has at most {MaxReason} characters; this one has {reason.Length}");
        if (reason is not null && change.Task == false) return Error("--no-task and --reason can't go together; a reason belongs to a closed to-do");

        return await WriteAsync(path, check: true, c =>
        {
            if (c.Card)
            {
                if (change.When.IsGiven || change.Until.IsGiven) return Plan.Refused(CardDue(c.Path));
                if (change.Task is not null || change.Tz.IsGiven || change.Reason.IsGiven) return Plan.Refused(CardStatus(c.Path));
            }

            // A time with an offset is written as local time: in the zone the note will have, else the caller's.
            var timeZone = change.Tz.IsGiven ? tz ?? c.Zone : Zones.Find(c.Values.GetValueOrDefault("tz") as string) ?? c.Zone;
            List<(string Key, string? Yaml)> changes = [];
            List<string> said = [];
            if (when is not null)
            {
                var written = when.Local(timeZone).Write();
                changes.Add(("when", written));
                said.Add($"when {written}");
            }

            if (change.Until.IsGiven)
            {
                var written = until?.Local(timeZone).Write();
                changes.Add(("until", written));
                said.Add(written is null ? "no until" : $"until {written}");
            }

            var task = TaskOf(c.Values);
            if (change.Task == true && task != "closed")
            {
                changes.Add(("task", "open"));
                said.Add("task open");
            }
            else if (change.Task == false)
            {
                changes.AddRange([("task", null), ("closed", null), ("reason", null)]);
                said.Add("no task");
            }

            if (change.Reason.IsGiven)
            {
                if (reason is not null && task != "closed") return Plan.Refused(Error($"{c.Path.Value} is not closed; calendar close <note> --reason <text> closes it with one"));
                changes.Add(("reason", reason is null ? null : FrontmatterWrites.Scalar(reason)));
                said.Add(reason is null ? "no reason" : $"reason {Shown(reason)}");
            }

            if (change.Upcoming.IsGiven)
            {
                changes.Add(("upcoming", upcoming?.ToString()));
                said.Add(upcoming is null ? "no upcoming" : $"upcoming {upcoming}");
            }

            if (change.Tz.IsGiven)
            {
                changes.Add(("tz", tz?.Id));
                said.Add(tz is null ? "no tz" : $"tz {tz.Id}");
            }

            return new Plan(changes, $"set {string.Join(", ", said)} on {c.Path.Value}");
        }, ct);
    }

    /// <summary>A new <c>when</c>; a window keeps its length (<c>until</c> moves with it).</summary>
    public async Task<ShellResult> MoveAsync(string path, string when, CancellationToken ct)
    {
        if (!CalendarTime.TryParse((when ?? "").Trim(), out var target)) return Error($"{when} is not a date; {DateHint}");

        return await WriteAsync(path, check: true, c =>
        {
            if (c.Card) return Plan.Refused(CardDue(c.Path));
            if (NotAnEntry(c) is { } refused) return Plan.Refused(refused);
            return Moved(c, target.Local(c.Entry!.Zone ?? c.Zone));
        }, ct);
    }

    public async Task<ShellResult> CloseAsync(string path, string? reason, CancellationToken ct)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > MaxReason }) return Error($"a reason has at most {MaxReason} characters; this one has {reason.Length}");

        return await WriteAsync(path, check: true, c =>
        {
            if (c.Card) return Plan.Refused(CardStatus(c.Path));
            var task = TaskOf(c.Values);
            if (task is not ("open" or "closed")) return Plan.Refused(NotATask(c.Path));
            if (task == "closed")
            {
                var day = c.Values.GetValueOrDefault("closed") is string closed && CalendarTime.TryParse(closed, out var read) ? $" ({Human(read, c.Today)})" : "";
                return reason is null
                    ? new Plan([], "", Unchanged: $"{c.Path.Value} is closed already{day}")
                    : new Plan([("reason", FrontmatterWrites.Scalar(reason))], $"gave {c.Path.Value} the reason {Shown(reason)}");
            }

            List<(string Key, string? Yaml)> changes = [("task", "closed"), ("closed", Day(c.Today))];
            if (reason is not null) changes.Add(("reason", FrontmatterWrites.Scalar(reason)));
            return new Plan(changes, $"closed {c.Path.Value} ({c.Today.ToString("d MMM", Invariant)})");
        }, ct);
    }

    public async Task<ShellResult> ReopenAsync(string path, CancellationToken ct) =>
        await WriteAsync(path, check: true, c =>
        {
            if (c.Card) return Plan.Refused(CardStatus(c.Path));
            var task = TaskOf(c.Values);
            if (task is not ("open" or "closed")) return Plan.Refused(NotATask(c.Path));
            return new Plan([("task", "open"), ("closed", null), ("reason", null)], $"reopened {c.Path.Value}", Unchanged: $"{c.Path.Value} is open already");
        }, ct);

    /// <summary>Takes a note out of the calendar: every calendar key goes, the note stays.</summary>
    public async Task<ShellResult> UnsetAsync(string path, CancellationToken ct) =>
        await WriteAsync(path, check: false, c =>
        {
            if (c.Card) return Plan.Refused(CardDue(c.Path));
            if (EntryReader.IsDayOrWeekName(c.Path.Name, out var byName))
            {
                var what = byName.Kind == TimeKind.Week ? $"week note of week {byName.IsoWeek}" : $"day note of {byName.Date.ToString("d MMMM", Invariant)}";
                return Plan.Refused(Error($"{c.Path.Value} is the {what} by its name; rename it to take it out of the calendar"));
            }

            return new Plan([.. Keys.Select(k => (k, (string?)null))], $"took {c.Path.Value} out of the calendar; the note stays",
                Unchanged: $"{c.Path.Value} is not in the calendar; nothing changed");
        }, ct);

    /// <summary>
    /// Later (spec §7.13): <paramref name="step"/> counts from the later of today and the entry's current <c>when</c> (Postpone) or
    /// end (More time), so nothing moves earlier; <paramref name="date"/> (Pick a date…) gives the new day directly. A time keeps its
    /// clock. On a card, Postpone sets its <c>due</c> through the board.
    /// </summary>
    public async Task<ShellResult> LaterAsync(string path, LaterPart part, LaterStep? step, string? date, CancellationToken ct)
    {
        if ((step is null) == string.IsNullOrWhiteSpace(date)) return Error("Later takes +1 day, +1 week or a date: one of them");
        CalendarTime? picked = null;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!CalendarTime.TryParse(date.Trim(), out var parsed) || parsed.Kind == TimeKind.Time) return Error($"{date} is not a day; give 2026-11-20");
            picked = parsed;
        }

        if (!TryNote(path, out var notePath, out var error)) return error;
        if (Refuse(notePath) is { } refused) return refused;
        if (await store.GetAsync(notePath, ct) is { } note && Frontmatter.Read(note.Content) is { Problem: null } read && EntryReader.IsCard(notePath, read.Values))
            return await LaterCardAsync(notePath, read.Values, part, step, picked, ct);

        return await WriteAsync(path, check: true, c =>
        {
            if (c.Card) return Plan.Refused(CardDue(c.Path));
            if (NotAnEntry(c) is { } notAnEntry) return Plan.Refused(notAnEntry);
            var entry = c.Entry!;
            if (entry.Task == TaskMark.Closed) return Plan.Refused(Error($"{c.Path.Value} is closed; calendar reopen <note> opens it again"));
            var timeZone = entry.Zone ?? c.Zone;
            if (part == LaterPart.Postpone)
            {
                var (target, laterError) = Later(c.Path, entry.When.Local(timeZone), step, picked, c.Today);
                return target is null ? Plan.Refused(laterError!) : Moved(c, target);
            }

            if (entry.Until is not { } until) return Plan.Refused(Error($"{c.Path.Value} has no until, so its when is its end: Postpone moves it"));
            var (end, endError) = Later(c.Path, until.Local(timeZone), step, picked, c.Today);
            if (end is null) return Plan.Refused(endError!);
            return new Plan([("until", end.Write())], $"gave {c.Path.Value} more time, until {Human(end, c.Today)}");
        }, ct);
    }

    /// <summary>The day note of today in the caller's zone, anywhere in the vault (several: the last changed), else a new one in
    /// <c>Calendar/</c> that starts with its date (spec §7.5).</summary>
    public async Task<ShellResult> TodayAsync(string vault, CancellationToken ct)
    {
        var today = await TodayAsync(ct);
        return await DayOrWeekNoteAsync(vault, today.ToString("yyyy-MM-dd", Invariant) + ".md", $"# {today.ToString("dddd, d MMMM yyyy", Invariant)}\n\n", "today's day note", ct);
    }

    /// <summary>The week note of this week, as <see cref="TodayAsync(string, CancellationToken)"/> finds or makes the day note.</summary>
    public async Task<ShellResult> ThisWeekAsync(string vault, CancellationToken ct)
    {
        var today = (await TodayAsync(ct)).ToDateTime(TimeOnly.MinValue);
        var week = CalendarTime.Week(ISOWeek.GetYear(today), ISOWeek.GetWeekOfYear(today));
        var (first, last) = (week.FirstDay, week.LastDay);
        var range = first.Year != last.Year ? $"{first.ToString("d MMMM yyyy", Invariant)} – {last.ToString("d MMMM yyyy", Invariant)}"
            : first.Month != last.Month ? $"{first.ToString("d MMMM", Invariant)} – {last.ToString("d MMMM yyyy", Invariant)}"
            : $"{first.Day}–{last.ToString("d MMMM yyyy", Invariant)}";
        return await DayOrWeekNoteAsync(vault, week.Write() + ".md", $"# Week {week.IsoWeek}, {range}\n\n", "this week's week note", ct);
    }

    /// <summary>A new entry <c>/&lt;vault&gt;/Calendar/&lt;title&gt;.md</c> on <paramref name="day"/> (at <paramref name="time"/>), a to-do
    /// when <paramref name="task"/>; a title taken gives "Title 2", "Title 3" (spec §7.5).</summary>
    public async Task<ShellResult> AddAsync(string vault, string title, string day, string? time, bool task, string? upcoming, CancellationToken ct)
    {
        title = (title ?? "").Trim().Normalize(NormalizationForm.FormC);
        if (title.Length == 0) return Error("an entry needs a title (it becomes the note's name)");
        if (title.Contains('/') || title.StartsWith('.')) return Error("a title can't contain / or start with a dot");
        if (!CalendarTime.TryParse((day ?? "").Trim(), out var when) || when.Kind != TimeKind.Day) return Error($"{day} is not a day; give 2026-11-20");
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (!TimeOnly.TryParseExact(time.Trim(), ["HH:mm", "H:mm"], Invariant, DateTimeStyles.None, out var clock)) return Error($"{time} is not a time; give 14:00");
            when = new CalendarTime(TimeKind.Time, when.Date, clock);
        }

        Upcoming? early = null;
        if (!string.IsNullOrWhiteSpace(upcoming) && !Upcoming.TryParse(upcoming.Trim(), out early)) return Error($"{upcoming} is not a span; give 7d, 2w or 12h");
        if (await VaultAsync(vault, ct) is not { } root) return Error($"'{vault}' is not a vault (ls / lists them)", ShellErrorKind.NotFound);

        var folder = root.Child("Calendar");
        var first = folder.Child(title + ".md");
        if (NoteNames.Problem(first) is { } problem) return Error($"{first.Value}: {problem}");
        if (Refuse(first) is { } refused) return refused;

        var taken = (await store.ListNotesUnderAsync(folder, ct)).Where(p => p.Segments.Count == folder.Segments.Count + 1).Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var path = first;
        for (var n = 2; taken.Contains(path.Name) || await store.IsDamagedAsync(path, ct); n++) path = folder.Child($"{title} {n}.md");

        List<(string Key, string? Yaml)> keys = [("when", when.Write())];
        if (task) keys.Add(("task", "open"));
        if (early is not null) keys.Add(("upcoming", early.ToString()));
        var result = await writer.WriteAsync(path.Value, Frontmatter.Set($"# {title}\n", keys), null, ct);
        if (result.IsError) return AsCalendar(result);
        return ShellResult.Ok($"{Tool}: created {path.Value} ({Human(when, await TodayAsync(ct))})") with { Version = result.Version };
    }

    private async Task<ShellResult> DayOrWeekNoteAsync(string vault, string name, string heading, string what, CancellationToken ct)
    {
        if (await VaultAsync(vault, ct) is not { } root) return Error($"'{vault}' is not a vault (ls / lists them)", ShellErrorKind.NotFound);

        StoredNote? found = null;
        foreach (var candidate in await store.ListNotesUnderAsync(root, ct))
        {
            if (candidate.Name != name || ArchiveFolder.Contains(candidate)) continue;
            if (await store.GetAsync(candidate, ct) is not { } note) continue;
            if (found is null || note.LastWriteAt > found.LastWriteAt
                || note.LastWriteAt == found.LastWriteAt && string.CompareOrdinal(note.Path.Value, found.Path.Value) < 0)
                found = note;
        }

        if (found is not null) return Written($"{found.Path.Value} is {what}", found);

        var path = root.Child("Calendar").Child(name);
        if (Refuse(path) is { } refused) return refused;
        var result = await writer.WriteAsync(path.Value, heading, null, ct);
        return result.IsError ? AsCalendar(result) : ShellResult.Ok($"{Tool}: created {path.Value}, {what}") with { Version = result.Version };
    }

    /// <summary>Later on a card: Postpone only, its <c>due</c> set through the board (a card has no <c>until</c>, and its date is a day).</summary>
    private async Task<ShellResult> LaterCardAsync(
        VaultPath path, IReadOnlyDictionary<string, object?> values, LaterPart part, LaterStep? step, CalendarTime? picked, CancellationToken ct)
    {
        if (part == LaterPart.MoreTime) return Error($"{path.Value} is a card; a card has no until: Postpone moves its due");
        if (picked is { Kind: TimeKind.Week }) return Error($"{path.Value} is a card, and its due is a day; give a day such as 2026-11-20");
        if (values.GetValueOrDefault("due") is not string written || !CalendarTime.TryParse(written, out var due) || due.Kind != TimeKind.Day)
            return Error($"{path.Value} is a card without a due that can be read; set one with the board tool");

        var today = await TodayAsync(ct);
        var (target, error) = Later(path, due, step, picked, today);
        if (target is null) return error!;
        var result = await board.UpdateAsync(path.Segments[0], path.Value, new Optional<DateOnly?>(target.Date), default, ct);
        if (result.IsError) return AsCalendar(result);
        return ShellResult.Ok($"{Tool}: moved {path.Value} to {Human(target, today)}") with { Version = result.Version };
    }

    /// <summary>The value Later moves <paramref name="value"/> to: +1 day or +1 week from the later of today and the value, or the
    /// picked date in the value's kind (a time keeps its clock, a week takes the week of the day).</summary>
    private static (CalendarTime? Value, ShellResult? Error) Later(VaultPath path, CalendarTime value, LaterStep? step, CalendarTime? picked, DateOnly today)
    {
        if (picked is { } date)
        {
            CalendarTime? target = (value.Kind, date.Kind) switch
            {
                (TimeKind.Week, TimeKind.Day) => WeekOf(date.Date),
                (TimeKind.Week, _) => date,
                (_, TimeKind.Week) => null,
                (TimeKind.Time, _) => value with { Date = date.Date },
                _ => date,
            };
            return target is null ? (null, Error($"{path.Value} is on a day; give a day such as 2026-11-20")) : (target, null);
        }

        if (value.Kind == TimeKind.Week && step == LaterStep.Day) return (null, Error($"{path.Value} is a week; +1 week moves it, +1 day is not offered"));
        // A week compares by its Monday, so today's week counts as today.
        var from = value.Kind == TimeKind.Week ? WeekOf(today).FirstDay : today;
        var start = value.FirstDay > from ? value : value with { Date = from };
        var days = step == LaterStep.Week ? 7 : 1;
        return start.CanAddDays(days) ? (start.AddDays(days), null) : (null, Error("a calendar has the years 2 to 9998"));
    }

    private static CalendarTime WeekOf(DateOnly day)
    {
        var at = day.ToDateTime(TimeOnly.MinValue);
        return CalendarTime.Week(ISOWeek.GetYear(at), ISOWeek.GetWeekOfYear(at));
    }

    /// <summary>The changes that give the entry <paramref name="target"/> as its <c>when</c>; its <c>until</c> moves by as much, so a
    /// window keeps its length (and a time its clock when it moves by whole days).</summary>
    private static Plan Moved(Current c, CalendarTime target)
    {
        var entry = c.Entry!;
        List<(string Key, string? Yaml)> changes = [("when", target.Write())];
        if (entry.Until is { } until)
        {
            var timeZone = entry.Zone ?? c.Zone;
            var from = entry.When.Local(timeZone);
            if (target.Kind != from.Kind)
            {
                var (kind, example) = from.Kind switch
                {
                    TimeKind.Day => ("day", "2026-11-20"),
                    TimeKind.Time => ("time", "2026-11-20T14:00"),
                    _ => ("week", "2026-W47"),
                };
                return Plan.Refused(Error($"{c.Path.Value} runs from {kind} to {kind}; move it to a {kind} such as {example}"));
            }

            var end = until.Local(timeZone);
            var shift = target.Date.ToDateTime(target.Clock) - from.Date.ToDateTime(from.Clock);
            // Within the years the day moves to, a part of a day more can't leave DateTime's range.
            if (!end.CanAddDays(shift.Days)) return Plan.Refused(Error("a calendar has the years 2 to 9998"));
            var moved = end.Date.ToDateTime(end.Clock) + shift;
            if (moved.Year is <= 1 or >= 9999) return Plan.Refused(Error("a calendar has the years 2 to 9998"));
            var next = end.Kind == TimeKind.Time
                ? new CalendarTime(TimeKind.Time, DateOnly.FromDateTime(moved), TimeOnly.FromDateTime(moved))
                : end with { Date = DateOnly.FromDateTime(moved) };
            changes.Add(("until", next.Write()));
        }

        return new Plan(changes, $"moved {c.Path.Value} to {Human(target, c.Today)}");
    }

    /// <summary>A note as a write plans on it, read just now; <see cref="Values"/> has the calendar's keys in lower case.</summary>
    private sealed record Current(VaultPath Path, IReadOnlyDictionary<string, object?> Values, Entry? Entry, CalendarProblem? Problem, bool Card,
        TimeZoneInfo Zone, DateOnly Today);

    /// <summary>The keys to change and what to answer, or why not; <see cref="Unchanged"/> answers when nothing changes.</summary>
    private sealed record Plan(IReadOnlyList<(string Key, string? Yaml)> Changes, string Message, ShellResult? Error = null, string? Unchanged = null)
    {
        public static Plan Refused(ShellResult error) => new([], "", error);
    }

    /// <summary>
    /// Reads the note, plans the change on what it holds now, checks it (<paramref name="check"/>: the note must stay an entry whose
    /// dates can be read) and writes it with compare-and-swap; on a write in between, all of it again on the new state.
    /// </summary>
    private async Task<ShellResult> WriteAsync(string pathText, bool check, Func<Current, Plan> plan, CancellationToken ct)
    {
        if (!TryNote(pathText, out var path, out var error)) return error;
        if (Refuse(path) is { } refused) return refused;
        var callerZone = await zone.ZoneAsync(ct);
        var today = Zones.ToLocal(now(), callerZone).Day;
        var vault = path.Segments[0];

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var note = await store.GetAsync(path, ct);
            if (note is null)
            {
                return await store.IsDamagedAsync(path, ct)
                    ? ShellResult.Error(Damage.Message(Tool, path)) with { Kind = ShellErrorKind.Conflict }
                    : Error($"{path.Value}: No such note", ShellErrorKind.NotFound);
            }

            var read = Frontmatter.Read(note.Content);
            if (read.Problem is not null)
                return Error($"{path.Value}: the frontmatter does not parse, so the calendar leaves it alone (fix it with edit): {read.Problem}", ShellErrorKind.Conflict);
            var (entry, problem) = EntryReader.Read(new Note(path, note.Content), null);
            // The plan reads the calendar's keys in any case; the write keeps the spelling the note has.
            var current = new Current(path, CalendarKeys.Fold(read.Values).Values, entry, problem, EntryReader.IsCard(path, read.Values), callerZone, today);

            var planned = plan(current);
            if (planned.Error is not null) return planned.Error;
            var changes = CalendarKeys.Spelled(read.Values, planned.Changes);
            if (!FrontmatterWrites.TryOwnedKeys(Tool, path, note.Content, changes, out var content, out error)) return error;
            // A block that held only calendar keys goes with them; one that was empty before stays.
            if (EmptyBlock(content) is var empty and > 0 && EmptyBlock(note.Content) == 0) content = content[empty..];
            if (content == note.Content) return Written(planned.Unchanged ?? $"{path.Value} is unchanged", note);
            if (check && !current.Card)
            {
                var (after, afterProblem) = EntryReader.Read(new Note(path, content), null);
                if (afterProblem is not null) return Error($"{path.Value}: {afterProblem.What}; nothing was written");
                if (after is null) return Error($"{path.Value} has no date yet; give it one with --when");
            }

            var next = NoteWriter.Next(note, content, Caller.WriterIn(vault), Caller.ReferenceIn(vault), writer.Now, await writer.ChangesetAsync(path, ct));
            if (!await store.TryReplaceAsync(note, next, ct)) continue;
            return Written(planned.Message, next);
        }

        return Error($"{path.Value} kept changing; try again", ShellErrorKind.Conflict);
    }

    private async Task<DateOnly> TodayAsync(CancellationToken ct) => Zones.ToLocal(now(), await zone.ZoneAsync(ct)).Day;

    private async Task<VaultPath?> VaultAsync(string vault, CancellationToken ct)
    {
        var name = (vault ?? "").Trim().Trim('/');
        if (name.Length == 0 || name.Contains('/') || !VaultPath.TryParse("/" + name, out var root) || root.Segments.Count != 1) return null;
        return (await store.ListVaultsAsync(ct)).Contains(root.Segments[0]) ? root : null;
    }

    private static bool TryNote(string text, out VaultPath path, out ShellResult error)
    {
        error = ShellResult.Ok("");
        if (VaultPath.TryParse(text ?? "", out var parsed) && parsed.Segments.Count >= 2 && parsed.IsNote)
        {
            path = parsed;
            return true;
        }

        path = VaultPath.Root;
        error = Error($"{text} is not a note's path; give one such as /Work/Plan.md");
        return false;
    }

    private static string? TaskOf(IReadOnlyDictionary<string, object?> values) =>
        values.GetValueOrDefault("task") is string task ? task.Trim().ToLowerInvariant() : null;

    private static string? Given(Optional<string?> value) => value.IsGiven && !string.IsNullOrWhiteSpace(value.Value) ? value.Value.Trim() : null;

    private static ShellResult? NotAnEntry(Current c) =>
        c.Entry is not null ? null
        : c.Problem is { } problem ? Error($"{c.Path.Value}: {problem.What}; calendar set <note> --when <value> writes a valid one")
        : Error($"{c.Path.Value} is not in the calendar; calendar set <note> --when <value> gives it a date");

    private static ShellResult NotATask(VaultPath path) => Error($"{path.Value} is not a to-do; calendar set <note> --task makes it one");

    private static ShellResult CardStatus(VaultPath path) =>
        Error($"{path.Value} is a card; on a card the column is the status: move it with the board tool");

    private static ShellResult CardDue(VaultPath path) => Error($"{path.Value} is a card; its date is its due: change it with the board tool");

    /// <summary>"17 Dec", "17 Dec 14:00", "Week 47"; with the year when it isn't this one.</summary>
    private static string Human(CalendarTime value, DateOnly today)
    {
        var year = value.Kind == TimeKind.Week ? value.IsoYear : value.Date.Year;
        var suffix = year == today.Year ? "" : " " + year.ToString(Invariant);
        return value.Kind switch
        {
            TimeKind.Week => $"Week {value.IsoWeek}{suffix}",
            TimeKind.Time => value.Date.ToString("d MMM", Invariant) + suffix + " " + value.Clock.ToString("HH:mm", Invariant),
            _ => value.Date.ToString("d MMM", Invariant) + suffix,
        };
    }

    /// <summary>The length of an empty frontmatter block at the start of <paramref name="content"/>, in either line ending; 0 without one.</summary>
    private static int EmptyBlock(string content) =>
        content.StartsWith("---\n---\n", StringComparison.Ordinal) ? "---\n---\n".Length
        : content.StartsWith("---\r\n---\r\n", StringComparison.Ordinal) ? "---\r\n---\r\n".Length
        : 0;

    /// <summary>A reason as an answer shows it back: on one line and cut short.</summary>
    private static string Shown(string reason)
    {
        var line = string.Join(' ', reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        line = new string([.. line.Select(c => char.IsControl(c) ? ' ' : c)]);
        return line.Length > ShownReason ? line[..ShownReason].TrimEnd() + "…" : line;
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", Invariant);

    private ShellResult? Refuse(VaultPath path) => Caller.Refuse(Tool, path) is { } refused ? refused with { Kind = ShellErrorKind.Refused } : null;

    /// <summary>An answer of the note writer or the board, in the calendar's name.</summary>
    private static ShellResult AsCalendar(ShellResult result)
    {
        var colon = result.Output.IndexOf(": ", StringComparison.Ordinal);
        var output = colon > 0 && result.Output[..colon] is "write" or "board" ? $"{Tool}: {result.Output[(colon + 2)..]}" : result.Output;
        return result with { Output = output, Kind = result.Kind ?? ShellErrorKind.Conflict };
    }

    private static ShellResult Error(string message, ShellErrorKind kind = ShellErrorKind.Invalid) =>
        ShellResult.Error($"{Tool}: {message}") with { Kind = kind };

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok($"{Tool}: {message}") with { Version = new NoteVersion(note.Path.Value, note.Version) };
}
