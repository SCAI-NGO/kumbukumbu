// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>The person's calendar settings: <c>zone</c>, the IANA zone she chose (null: none, so the server's applies);
/// <c>serverZone</c>, the server's.</summary>
public sealed record CalendarPreferencesView(string? Zone, string ServerZone);

/// <summary>A change of the zone: an IANA id such as <c>Europe/Vienna</c>, or null to clear it.</summary>
public sealed record CalendarZoneRequest(string? Zone);

/// <summary><c>view</c>: <c>week</c> or <c>month</c>; <c>grid</c>: the month grid shown.</summary>
public sealed record CalendarViewRequest(string View, bool Grid);

/// <summary>
/// An entry as the caller sees it, in her zone. <c>kind</c>: <c>dated</c>, <c>task</c> or <c>card</c>; <c>when</c> and <c>until</c>
/// as Kumbu² writes them (a time with an offset as its local time); <c>task</c>: <c>none</c>, <c>open</c> or <c>closed</c> (a card's
/// from its column); <c>state</c>: <c>notYet</c>, <c>upcoming</c>, <c>ongoing</c>, <c>overdue</c>, <c>done</c> or <c>past</c>, with
/// <c>span</c> beside it ("in 7 days", "until Fri", "3 weeks"); <c>time</c>: a timed entry's clock, "14:00", or "14:00 (Europe/London)"
/// when its <c>tz</c> pins it to another zone; <c>firstDay</c>/<c>lastDay</c>: the caller's days it covers, <c>spans</c> when they
/// differ or it is a week; <c>boardAction</c>: on a card, the board's one archivable column it can be moved to, if there is exactly one;
/// <c>beforeTime</c>: ongoing on its day before its time, <c>span</c> then the clock ("today · 14:00").
/// </summary>
public sealed record EntryView(string Path, string Vault, string Title, string Kind, string When, string? Until,
    string Task, string? Closed, string? Reason, string? Upcoming, string? Tz, bool ByName, string? Column,
    string State, string? Span, string? Time, string FirstDay, string LastDay, bool Spans, string? BoardAction, bool BeforeTime = false);

/// <summary>A note whose date couldn't be read: <c>plain</c> says what is wrong in plain words; <c>readable</c> holds the
/// calendar keys that could be read, as written (for Fix…).</summary>
public sealed record CouldntReadView(string Path, string Title, string Plain, IReadOnlyDictionary<string, string> Readable);

/// <summary>A day's own entries, and the entries spanning several days that touch it.</summary>
public sealed record DayView(string Day, IReadOnlyList<EntryView> Entries, IReadOnlyList<EntryView> AlsoOngoing);

/// <summary>An ISO week: <c>spanning</c> its week entries and those over several days; <c>days</c> Monday to Sunday;
/// <c>current</c> when it holds the caller's today.</summary>
public sealed record WeekView(int Year, int Week, string Monday, bool Current, IReadOnlyList<EntryView> Spanning, IReadOnlyList<DayView> Days);

/// <summary>A vault's calendar: one week, or a month as its weeks. <c>today</c> is the caller's day in <c>zone</c>;
/// <c>canWrite</c> whether she may change the vault's entries.</summary>
public sealed record CalendarView(string Vault, string Today, string Zone, IReadOnlyList<WeekView> Weeks, IReadOnlyList<CouldntReadView> CouldntRead, bool CanWrite);

/// <summary>What's on over every vault the session shows: <c>count</c> and <c>overdue</c> are the badge's, <c>vaults</c> each
/// vault's share (only vaults with something on); <c>couldntRead</c> how many dates couldn't be read, in <c>couldntReadVaults</c>.</summary>
public sealed record WhatsOnView(IReadOnlyList<EntryView> Items, int Count, bool Overdue, IReadOnlyList<VaultCountView> Vaults, int CouldntRead, IReadOnlyList<string> CouldntReadVaults);

public sealed record VaultCountView(string Vault, int Count, bool Overdue);

/// <summary>A note's place in the calendar, for its header: its entry, or what couldn't be read (a card can have both), or
/// neither when it isn't an entry.</summary>
public sealed record NoteEntryView(EntryView? Entry, CouldntReadView? CouldntRead, bool CanWrite);

/// <summary>A change of a note's calendar keys (spec §6.2, §7.8): a key given is set; <c>clearUntil</c>, <c>clearUpcoming</c> and
/// <c>clearReason</c> remove theirs, and not together with it. <c>task</c> true makes it a to-do (a closed one stays closed),
/// false removes <c>task</c>, <c>closed</c> and <c>reason</c>. <c>when</c> can't be removed here: <c>unset</c> takes a note out.</summary>
public sealed record SetEntryRequest(string Path, string? When = null, string? Until = null, bool ClearUntil = false, bool? Task = null,
    string? Upcoming = null, bool ClearUpcoming = false, string? Reason = null, bool ClearReason = false);

public sealed record CalendarPathRequest(string Path);

public sealed record CloseRequest(string Path, string? Reason = null);

/// <summary>A new <c>when</c>; a window keeps its length.</summary>
public sealed record MoveEntryRequest(string Path, string When);

/// <summary>Later (spec §7.13): <c>part</c> <c>postpone</c> or <c>moreTime</c>; <c>step</c> <c>day</c> or <c>week</c>, or a
/// <c>date</c> (<c>2026-11-20</c>) instead.</summary>
public sealed record LaterRequest(string Path, string Part, string? Step = null, string? Date = null);

/// <summary>A day's + (spec §7.5): <c>/&lt;vault&gt;/Calendar/&lt;title&gt;.md</c> on <c>day</c> (<c>2026-11-20</c>), at
/// <c>time</c> (<c>14:00</c>); <c>task</c> makes it a to-do, shown <c>upcoming</c> (<c>7d</c>) early.</summary>
public sealed record NewEntryRequest(string Day, string Title, string? Time = null, bool Task = false, string? Upcoming = null);

/// <summary>The note a write changed, opened or made, with its new version and its place in the calendar.</summary>
public sealed record WrittenView(string Path, string Version, NoteEntryView Note);

/// <summary>The calendar for the web app (calendar spec §7): a vault's week or month, What's on and its badge, a note's entry,
/// the person's time zone and her last view per calendar. Notes are read on demand, through the session's store.</summary>
public static partial class CalendarApi
{
    private const string NoViewSentence = "Only a signed-in person keeps a calendar view.";

    public static void MapCalendarApi(this IEndpointRouteBuilder app)
    {
        // As /account/preferences: a person's setting, for her session only.
        var account = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("/account/calendar", PreferencesAsync).Errors(401, 403);
        account.MapPut("/account/calendar", SetZoneAsync).Errors(400, 401, 403);

        var vault = app.MapGroup("/api/vaults/{vault}/calendar").AddEndpointFilter(ApiErrors.Filter);
        // Polled while the page is open, as the board is: they don't count as the session's use.
        vault.MapGet("", CalendarAsync).WithMetadata(BackgroundMetadata.Instance).Errors(400, 401, 404, 503);
        vault.MapGet("entry", EntryAsync).WithMetadata(BackgroundMetadata.Instance).Errors(400, 401, 404, 503);
        vault.MapGet("view", ViewAsync).Errors(400, 401, 404, 503);
        vault.MapPut("view", SetViewAsync).Errors(400, 401, 403, 404, 409, 503);

        // The same writes as sh calendar, through CalendarService with the caller's rights.
        vault.MapPost("set", SetAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("move", MoveAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("close", CloseAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("reopen", ReopenAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("unset", UnsetAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("later", LaterAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("today", TodayAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("this-week", ThisWeekAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        vault.MapPost("entries", AddAsync).Errors(400, 401, 403, 404, 409, 503, 507);

        app.MapGroup("/api/calendar").AddEndpointFilter(ApiErrors.Filter)
            .MapGet("whats-on", WhatsOnAsync).WithMetadata(BackgroundMetadata.Instance).Errors(400, 401, 503);
    }

    /// <summary>The week <paramref name="week"/> (<c>2026-W46</c>) or the month <paramref name="month"/> (<c>2026-11</c>) as its
    /// weeks; neither: the caller's current week. <paramref name="tag"/> keeps the notes carrying it.</summary>
    private static async Task<Ok<CalendarView>> CalendarAsync(
        string vault, string? week, string? month, string? tag, INoteStore store, RequestCaller caller, ICallerZone callerZone,
        TimeProvider time, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        if (week is not null && month is not null) throw ApiErrors.BadRequest("Give a week or a month, not both.");
        var asked = Tag(tag);
        var askedWeek = week is null ? ((int Year, int Week)?)null : Week(week);
        var askedMonth = month is null ? ((int Year, int Month)?)null : Month(month);

        var zone = await callerZone.ZoneAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var today = Zones.ToLocal(now, zone).Day;
        var name = root.Segments[0];
        var calendar = await CalendarReader.ReadAsync(store, name, asked, ct);

        IReadOnlyList<CalendarWeek> weeks;
        if (askedMonth is { } shown) weeks = CalendarLayout.Month(calendar, shown.Year, shown.Month, now, zone);
        else
        {
            var day = today.ToDateTime(TimeOnly.MinValue);
            var (isoYear, isoWeek) = askedWeek ?? (ISOWeek.GetYear(day), ISOWeek.GetWeekOfYear(day));
            weeks = [CalendarLayout.Week(calendar, isoYear, isoWeek, now, zone)];
        }

        EntryView Shown(DayEntry item) => View(item.Entry, item.Status, zone, calendar.Board);
        return TypedResults.Ok(new CalendarView(
            name, Day(today), zone.Id,
            [.. weeks.Select(w => new WeekView(w.IsoYear, w.Week, Day(w.Monday), today >= w.Monday && today <= w.Monday.AddDays(6),
                [.. w.Spanning.Select(Shown)],
                [.. w.Days.Select(d => new DayView(Day(d.Day), [.. d.Entries.Select(Shown)], [.. d.AlsoOngoing.Select(Shown)]))]))],
            [.. calendar.Problems.Select(CouldntRead)],
            caller.CanWrite(name)));
    }

    /// <summary>
    /// What's on over every vault the caller's store lists: for a web session that leaves out the discreet vaults it doesn't
    /// show, so nothing of theirs is read, counted or coloured (spec §8). <paramref name="count"/>: the badge, without the items.
    /// </summary>
    private static async Task<Ok<WhatsOnView>> WhatsOnAsync(
        string? tag, bool? count, INoteStore store, ICallerZone callerZone, TimeProvider time, CancellationToken ct)
    {
        var asked = Tag(tag);
        var zone = await callerZone.ZoneAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var calendars = await CalendarReader.ReadAllAsync(store, asked, ct);
        var on = WhatsOn.Of(calendars, now, zone);

        var vaults = calendars
            .Select(c => (c.Vault, Items: on.Where(i => i.Entry.Vault == c.Vault).ToList()))
            .Where(v => v.Items.Count > 0)
            .Select(v => new VaultCountView(v.Vault, v.Items.Count, v.Items.Any(i => i.Status.State == EntryState.Overdue)))
            .ToList();
        var unread = calendars.Where(c => c.Problems.Count > 0).ToList();
        var boards = calendars.ToDictionary(c => c.Vault, c => c.Board, StringComparer.Ordinal);
        IReadOnlyList<EntryView> items = count == true ? [] : [.. on.Select(i => View(i.Entry, i.Status, zone, boards[i.Entry.Vault]))];

        return TypedResults.Ok(new WhatsOnView(
            items, on.Count, on.Any(i => i.Status.State == EntryState.Overdue), vaults,
            unread.Sum(c => c.Problems.Count), [.. unread.Select(c => c.Vault)]));
    }

    /// <summary>The entry of the note at <paramref name="path"/> (<c>/Work/x.md</c>, in <paramref name="vault"/>), for its header.</summary>
    private static async Task<Ok<NoteEntryView>> EntryAsync(
        string vault, string path, INoteStore store, RequestCaller caller, ICallerZone callerZone, TimeProvider time, CancellationToken ct)
    {
        var notePath = await NoteInAsync(vault, path, store, ct);
        return TypedResults.Ok(await NoteEntryAsync(notePath, store, caller, callerZone, time, ct));
    }

    /// <summary>The note <paramref name="path"/> in the caller's vault <paramref name="vault"/>: a vault she doesn't have, or
    /// that her session hides, is 404 before anything else; a path outside it is 400.</summary>
    internal static async Task<VaultPath> NoteInAsync(string vault, string? path, INoteStore store, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        var name = root.Segments[0];
        if (!VaultPath.TryParse(path ?? "", out var notePath) || notePath.Segments.Count < 2 || notePath.Segments[0] != name)
            throw ApiErrors.BadRequest($"{path} is not a note in {name}.");
        if (!notePath.IsNote) throw ApiErrors.BadRequest($"{notePath.Value} is no note: note paths end in .md.");
        return notePath;
    }

    private static async Task<NoteEntryView> NoteEntryAsync(
        VaultPath notePath, INoteStore store, RequestCaller caller, ICallerZone callerZone, TimeProvider time, CancellationToken ct)
    {
        var (_, entry, problem, board) = await ReadEntryAsync(notePath, store, ct);
        var zone = await callerZone.ZoneAsync(ct);
        var view = entry is null ? null : View(entry, EntryStates.Of(entry, time.GetUtcNow().UtcDateTime, zone), zone, board);
        return new NoteEntryView(view, problem is null ? null : CouldntRead(problem), caller.CanWrite(notePath.Segments[0]));
    }

    /// <summary>The note at <paramref name="notePath"/> as read, with its entry in the calendar read from that same state.</summary>
    internal static async Task<(StoredNote Note, Entry? Entry, CalendarProblem? Problem, BoardDefinition? Board)> ReadEntryAsync(
        VaultPath notePath, INoteStore store, CancellationToken ct)
    {
        var note = await store.GetAsync(notePath, ct) ?? throw await NoteApi.MissingAsync(store, notePath, ct);
        // A card's status is its column, and the board's definition says which one closes it.
        var definition = await store.GetAsync(BoardPaths.Definition(notePath.Segments[0]), ct);
        var board = definition is null ? null : BoardDefinition.Parse(definition.Content).Definition;
        var (entry, problem) = EntryReader.Read(new Note(notePath, note.Content), board);
        return (note, entry, problem, board);
    }

    private static async Task<Ok<WrittenView>> SetAsync(
        string vault, SetEntryRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var path = await NoteInAsync(vault, body.Path, store, ct);
        var change = new CalendarChange(
            When: body.When is null ? default : new Optional<string?>(body.When),
            Until: Change(body.Until, body.ClearUntil, "until"),
            Task: body.Task,
            Upcoming: Change(body.Upcoming, body.ClearUpcoming, "upcoming"),
            Reason: Change(body.Reason, body.ClearReason, "reason"));
        return TypedResults.Ok(await WrittenAsync(await calendar.SetAsync(path.Value, change, ct), store, caller, zone, time, ct));
    }

    /// <summary>A key left out is "not given"; removal is asked for explicitly.</summary>
    private static Optional<string?> Change(string? value, bool clear, string key)
    {
        var flag = "clear" + char.ToUpperInvariant(key[0]) + key[1..];
        if (value is not null && clear) throw ApiErrors.BadRequest($"Give {key} or {flag}, not both.");
        return value is not null ? new Optional<string?>(value) : clear ? new Optional<string?>(null) : default;
    }

    private static async Task<Ok<WrittenView>> MoveAsync(
        string vault, MoveEntryRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var path = await NoteInAsync(vault, body.Path, store, ct);
        return TypedResults.Ok(await WrittenAsync(await calendar.MoveAsync(path.Value, body.When, ct), store, caller, zone, time, ct));
    }

    private static async Task<Ok<WrittenView>> CloseAsync(
        string vault, CloseRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var path = await NoteInAsync(vault, body.Path, store, ct);
        return TypedResults.Ok(await WrittenAsync(await calendar.CloseAsync(path.Value, body.Reason, ct), store, caller, zone, time, ct));
    }

    private static async Task<Ok<WrittenView>> ReopenAsync(
        string vault, CalendarPathRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var path = await NoteInAsync(vault, body.Path, store, ct);
        return TypedResults.Ok(await WrittenAsync(await calendar.ReopenAsync(path.Value, ct), store, caller, zone, time, ct));
    }

    private static async Task<Ok<WrittenView>> UnsetAsync(
        string vault, CalendarPathRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var path = await NoteInAsync(vault, body.Path, store, ct);
        return TypedResults.Ok(await WrittenAsync(await calendar.UnsetAsync(path.Value, ct), store, caller, zone, time, ct));
    }

    /// <summary>Later, offered by the web app beside ✓ only (spec §7.13): on an open task, or a card not closed by its column.</summary>
    private static async Task<Ok<WrittenView>> LaterAsync(
        string vault, LaterRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var path = await NoteInAsync(vault, body.Path, store, ct);
        var part = body.Part switch
        {
            "postpone" => LaterPart.Postpone,
            "moreTime" => LaterPart.MoreTime,
            _ => throw ApiErrors.BadRequest("Later's part is postpone or moreTime."),
        };
        LaterStep? step = body.Step switch
        {
            null => null,
            "day" => LaterStep.Day,
            "week" => LaterStep.Week,
            _ => throw ApiErrors.BadRequest("Later's step is day or week."),
        };
        // The caller's rights first, as every other write: a read-only member hears 403 before anything about the entry.
        if (((ICaller)caller).Refuse("calendar", path) is { } refused) BoardApi.Check(refused with { Kind = ShellErrorKind.Refused }, "calendar");
        var (_, entry, _, _) = await ReadEntryAsync(path, store, ct);
        if (entry is not { Task: TaskMark.Open }) throw ApiErrors.BadRequest("Later is for something still to do.");
        return TypedResults.Ok(await WrittenAsync(await calendar.LaterAsync(path.Value, part, step, body.Date, ct), store, caller, zone, time, ct));
    }

    /// <summary>Today's day note (spec §7.5), found anywhere in the vault or made in <c>Calendar/</c>.</summary>
    private static async Task<Ok<WrittenView>> TodayAsync(
        string vault, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone, TimeProvider time, CancellationToken ct)
    {
        var name = await VaultNameAsync(vault, store, ct);
        return TypedResults.Ok(await WrittenAsync(await calendar.TodayAsync(name, ct), store, caller, zone, time, ct));
    }

    private static async Task<Ok<WrittenView>> ThisWeekAsync(
        string vault, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone, TimeProvider time, CancellationToken ct)
    {
        var name = await VaultNameAsync(vault, store, ct);
        return TypedResults.Ok(await WrittenAsync(await calendar.ThisWeekAsync(name, ct), store, caller, zone, time, ct));
    }

    private static async Task<Created<WrittenView>> AddAsync(
        string vault, NewEntryRequest body, INoteStore store, CalendarService calendar, RequestCaller caller, ICallerZone zone,
        TimeProvider time, CancellationToken ct)
    {
        var name = await VaultNameAsync(vault, store, ct);
        var written = await WrittenAsync(
            await calendar.AddAsync(name, body.Title, body.Day, body.Time, body.Task, body.Upcoming, ct), store, caller, zone, time, ct);
        return TypedResults.Created(ApiPaths.Url("notes", Written(written.Path)), written);
    }

    private static VaultPath Written(string path) =>
        VaultPath.TryParse(path, out var parsed) ? parsed : throw new InvalidOperationException($"A calendar write answered with the path {path}.");

    private static async Task<string> VaultNameAsync(string vault, INoteStore store, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        return root.Segments[0];
    }

    /// <summary>A write's answer as the board's (<see cref="BoardApi.Check"/>), else the note as it is now.</summary>
    private static async Task<WrittenView> WrittenAsync(
        ShellResult result, INoteStore store, RequestCaller caller, ICallerZone zone, TimeProvider time, CancellationToken ct)
    {
        BoardApi.Check(result, "calendar");
        var version = result.Version ?? throw new InvalidOperationException("A calendar write answered without the note's version.");
        var path = Written(version.Path);
        return new WrittenView(path.Value, version.Token, await NoteEntryAsync(path, store, caller, zone, time, ct));
    }

    private static EntryView View(Entry entry, EntryStatus status, TimeZoneInfo zone, BoardDefinition? board)
    {
        var (first, last) = CalendarLayout.DaysOf(entry, zone);
        var spans = entry.When.Kind == TimeKind.Week || last > first;
        return new EntryView(
            entry.Path.Value, entry.Vault, entry.Title,
            entry.Kind switch { EntryKind.Task => "task", EntryKind.Card => "card", _ => "dated" },
            Written(entry.When, entry, zone), entry.Until is { } until ? Written(until, entry, zone) : null,
            entry.Task switch { TaskMark.Open => "open", TaskMark.Closed => "closed", _ => "none" },
            entry.Closed is { } closed ? Day(closed) : null, entry.Reason, entry.Upcoming?.ToString(), entry.Zone?.Id, entry.ByName, entry.Column,
            status.State switch
            {
                EntryState.NotYet => "notYet",
                EntryState.Upcoming => "upcoming",
                EntryState.Ongoing => "ongoing",
                EntryState.Overdue => "overdue",
                EntryState.Done => "done",
                _ => "past",
            },
            status.Span,
            // Over several days the start's clock alone: the span says where it ends.
            entry.When.Kind == TimeKind.Time ? EntryText.Clock(entry, zone, withEnd: !spans) : null,
            Day(first), Day(last), spans, BoardAction(entry, board), status.BeforeTime);
    }

    // Kumbu² writes no offset: a time given with one is shown as the local time it is, in the note's tz, else the caller's zone.
    private static string Written(CalendarTime value, Entry entry, TimeZoneInfo zone) => value.Local(entry.Zone ?? zone).Write();

    /// <summary>"Move to Done" (spec §7.8): only when the board has exactly one archivable column, and the card isn't closed
    /// (in it, or in a reason column).</summary>
    private static string? BoardAction(Entry entry, BoardDefinition? board)
    {
        if (entry.Kind != EntryKind.Card || entry.Task == TaskMark.Closed || board is null) return null;
        return board.Columns.Where(c => c.Archivable).ToList() is [var only] && only.Name != entry.Column ? only.Name : null;
    }

    private static CouldntReadView CouldntRead(CalendarProblem problem) =>
        new(problem.Path.Value, problem.Title, problem.Plain, problem.Readable);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Tag(string? asked)
    {
        if (asked is null) return null;
        return TagFilter.Parse(asked) ?? throw ApiErrors.BadRequest(Sentence(TagFilter.NotATag(asked)));
    }

    [GeneratedRegex(@"\A([0-9]{4})-W([0-9]{2})\z")]
    private static partial Regex WeekForm();

    [GeneratedRegex(@"\A([0-9]{4})-([0-9]{2})\z")]
    private static partial Regex MonthForm();

    private const string Years = "A calendar has the years 2 to 9998.";

    // The years a value can be read in (CalendarTime), and real weeks and months only, as sh calendar takes them.
    private static (int Year, int Week) Week(string text)
    {
        if (WeekForm().Match(text) is not { Success: true } match) throw ApiErrors.BadRequest($"A week is written as 2026-W46, not {text}.");
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (year is < 2 or > 9998) throw ApiErrors.BadRequest(Years);
        var weeks = ISOWeek.GetWeeksInYear(year);
        if (number < 1 || number > weeks) throw ApiErrors.BadRequest($"Week {number} doesn't exist in {year} (it has {weeks}).");
        return (year, number);
    }

    private static (int Year, int Month) Month(string text)
    {
        var notAMonth = ApiErrors.BadRequest($"A month is written as 2026-11, not {text}.");
        if (MonthForm().Match(text) is not { Success: true } match) throw notAMonth;
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (number is < 1 or > 12) throw notAMonth;
        if (year is < 2 or > 9998) throw ApiErrors.BadRequest(Years);
        return (year, number);
    }

    private static string Sentence(string text) => char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static async Task<Ok<CalendarPreferencesView>> PreferencesAsync(
        RequestCaller caller, CalendarSettings settings, ServerZone server, CancellationToken ct) =>
        TypedResults.Ok(new CalendarPreferencesView(await settings.ZoneAsync(caller.User!.Id, ct), (await server.ZoneAsync(ct)).Id));

    private static async Task<Ok<CalendarPreferencesView>> SetZoneAsync(
        CalendarZoneRequest body, RequestCaller caller, CalendarSettings settings, ServerZone server, CancellationToken ct)
    {
        string? zone = null;
        if (body.Zone is not null)
        {
            zone = (Zones.Find(body.Zone) ?? throw ApiErrors.BadRequest(
                $"{body.Zone} is not a time zone; choose one from the list (an IANA name such as Europe/Vienna).")).Id;
        }

        await settings.SetZoneAsync(caller.User!.Id, zone, ct);
        return TypedResults.Ok(new CalendarPreferencesView(zone, (await server.ZoneAsync(ct)).Id));
    }

    /// <summary>Asked when the calendar opens; a credential without a person's user key (a key, a connection) reads the
    /// default.</summary>
    private static async Task<Ok<CalendarViewState>> ViewAsync(
        string vault, INoteStore store, RequestCaller caller, CalendarSettings settings, CancellationToken ct)
    {
        var id = await VaultIdAsync(vault, store, caller, ct);
        if (caller.User is not { } user || caller.UserKey is not { } userKey) return TypedResults.Ok(CalendarViewState.Default);
        return TypedResults.Ok(await settings.ViewAsync(user.Id, userKey, id, ct));
    }

    private static async Task<Ok<CalendarViewState>> SetViewAsync(
        string vault, CalendarViewRequest body, INoteStore store, RequestCaller caller, CalendarSettings settings, CancellationToken ct)
    {
        var id = await VaultIdAsync(vault, store, caller, ct);
        if (caller.User is not { } user || caller.UserKey is not { } userKey) throw new ApiException(StatusCodes.Status403Forbidden, NoViewSentence);
        if (!CalendarViewState.IsView(body.View)) throw ApiErrors.BadRequest($"A calendar's view is {CalendarViewState.Week} or {CalendarViewState.Month}.");
        var view = new CalendarViewState(body.View, body.Grid);
        try
        {
            await settings.SetViewAsync(user.Id, userKey, id, view, ct);
        }
        catch (CalendarViewConflictException e)
        {
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }

        return TypedResults.Ok(view);
    }

    /// <summary>The id of the caller's vault <paramref name="vault"/>; a vault she doesn't have, or that her session
    /// hides, is the same 404.</summary>
    private static async Task<string> VaultIdAsync(string vault, INoteStore store, RequestCaller caller, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        return (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => v.Name == root.Segments[0])?.Id
            ?? throw ApiErrors.NotFound($"There is no vault '{root.Segments[0]}'.");
    }
}
