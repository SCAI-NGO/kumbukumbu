// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// <c>calendar</c> for agents (spec §6): a vault's week or month, and what's on over every vault the caller can read, in the
/// caller's zone; with a <paramref name="service"/>, also the write forms (spec §6.2). Without one (a checkpoint's reader) the
/// write forms are still recognised, so that a checkpoint can refuse them by name.
/// </summary>
public sealed partial class CalendarCommand(INoteReader notes, Func<DateTime> now, ICallerZone zone, CalendarService? service = null) : ICommand
{
    private static readonly Option[] ReadSpec = [new(null, "week", TakesValue: true), new(null, "month", TakesValue: true), new(null, "tag", TakesValue: true)];
    private static readonly Option[] NotificationsSpec = [new(null, "tag", TakesValue: true)];

    private static readonly Option[] SetSpec =
    [
        new(null, "when", TakesValue: true), new(null, "until", TakesValue: true), new(null, "no-until"), new(null, "task"), new(null, "no-task"),
        new(null, "upcoming", TakesValue: true), new(null, "tz", TakesValue: true), new(null, "reason", TakesValue: true),
    ];

    private static readonly Option[] CloseSpec = [new(null, "reason", TakesValue: true)];

    private static readonly string[] WriteVerbs = ["set", "move", "close", "reopen", "unset"];

    private const string Notifications = "notifications";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public string Name => "calendar";

    public string Usage => "calendar <vault> [--week W | --month M] [--tag T] | calendar notifications";

    public string Summary => "the notes with a time: a vault's week or month; notifications: what's on in every vault; set, move, close, reopen, unset change one";

    public string Help => """
        calendar <vault> [--week 2026-W46 | --week 46 | --month 2026-11] [--tag <tag>]
        calendar notifications [--tag <tag>]
        calendar set <note> --when <value> [--until <value>] [--task | --no-task] [--upcoming 7d] [--tz <zone>] [--reason <text>]
        calendar move <note> <when>
        calendar close <note> [--reason <text>]
        calendar reopen <note>
        calendar unset <note>
          Every vault has a calendar: its notes with a time. A note is an entry when its frontmatter
          has 'when' (a day 2026-11-20, a time 2026-11-20T14:00 or a week 2026-W47), or when it is
          named as a day or week (2026-11-12.md, 2026-W46.md) anywhere in the vault; 'until' ends it,
          'task: open' makes it a to-do, 'upcoming: 7d' shows it that early, 'tz' pins its times to
          a zone. A board card with 'due' is an entry too; its column is its status. Notes in
          _archive/ are left out. Days and times are in your time zone.
          calendar <vault> prints the current week: entries over several days and week notes first,
          with their span, then each day with its entries (time, title, path, what it is, and
          'upcoming', 'ongoing' or 'overdue' with a span), then the problems: dates that can't be
          read, never dropped.
          --week W   that week: 46 of the current year, or 2026-W46
          --month M  that month as its weeks, only the days with entries: 2026-11
          --tag T    only the notes with that tag or one nested below it, frontmatter or text
                     (ignoring case, with or without '#'); read them with cat
          calendar notifications lists what's on in every vault you can read: overdue first
          (longest first), then ongoing (ending soonest first), then upcoming (soonest first), and
          how many dates can't be read. A vault after it narrows it to that vault.
          set makes a note an entry or changes it; move gives it a new 'when' (a window keeps its
          length); close marks a to-do done (today, with a reason if given) and reopen undoes it;
          unset takes a note out of the calendar, the note stays. Each write leaves a version.
          set sets each key it is given: --task makes a note a to-do (task: open), --no-task
          removes task, closed and reason, --no-until (or --until "") removes until, --reason
          adds or changes the reason of a closed to-do. Values are checked and written in
          Kumbu²'s form; every other line of the note stays as it is. A note is given by its path.
          On a board card the column is the status: move it with the board tool; its date is its
          due, which the board tool changes too. Only --upcoming can be set on a card.

          calendar /Work
          calendar /Work --week 47
          calendar /Work --month 2026-12 --tag kumbu
          calendar notifications
          calendar set "/Work/Pump/Certificate.md" --when 2026-12-15 --task --upcoming 14d
          calendar close "/Work/Pump/Certificate.md" --reason "renewed until 2027"
          calendar move /Work/Urlaub.md 2026-12-21
        """;

    public bool ReadsInput => false;

    /// <summary>The write verb <paramref name="args"/> start with (options aside), or null for a read form.</summary>
    /// <summary>The write form a line names (set, move, close, reopen, unset), read past options as the command reads it; null for a read.</summary>
    public static string? WriteVerb(IReadOnlyList<string> args) => FirstWord(args) is { } word && WriteVerbs.Contains(word) ? word : null;

    // The first operand, past the options and their values: the vault, or the form's verb.
    private static string? FirstWord(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--") return i + 1 < args.Count ? args[i + 1] : null;
            if (args[i] is "--week" or "--month" or "--tag") i++;
            else if (!args[i].StartsWith('-')) return args[i];
        }

        return null;
    }

    internal static ShellResult CheckpointRefusal(string verb) => ShellResult.Error(
        $"calendar: {verb} changes a note, and a checkpoint can't be changed; --checkpoint works with calendar <vault> and calendar notifications");

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "a calendar is read as it is now, or from a checkpoint") is { } versionError) return versionError;
        if (WriteVerb(args) is { } verb)
        {
            return service is null
                ? ShellResult.Error($"calendar: {verb} is not available in this shell; calendar <vault> and calendar notifications read the calendar")
                : await WriteAsync(service, verb, args, ct);
        }

        return FirstWord(args) == Notifications ? await NotificationsAsync(args, ct) : await ReadAsync(args, ct);
    }

    private async Task<ShellResult> ReadAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, ReadSpec, out var options, out var error)) return error;
        if (options.Operands.Count == 0) return ShellResult.Error("calendar: missing operand (calendar <vault>, or calendar notifications)");
        if (options.Operands.Count > 1 && !options.Operands[0].StartsWith('/'))
        {
            return ShellResult.Error($"calendar: {options.Operands[0]} is not a form of calendar: calendar <vault>, calendar notifications, or set, move, close, reopen, unset (help calendar)");
        }

        if (options.Operands.Count > 1) return ShellResult.Error($"calendar: too many arguments{CommandArgs.QuoteHint}");
        var (vault, vaultError) = await VaultAsync(options.Operands[0], "calendar /Work", ct);
        if (vault is null) return vaultError!;
        if (options.Has("week") && options.Has("month")) return ShellResult.Error("calendar: --week and --month can't go together; give one");
        if (!TryTag(options, out var tag, out var tagError)) return tagError!;

        var zoneNow = await zone.ZoneAsync(ct);
        var nowUtc = now();
        var today = Zones.ToLocal(nowUtc, zoneNow).Day;

        (int Year, int Month)? month = null;
        (int Year, int Week) week;
        if (options.Value("month") is { } monthText)
        {
            if (ParseMonth(monthText, out var asked) is { } monthError) return monthError;
            month = asked;
            week = default;
        }
        else
        {
            var day = today.ToDateTime(TimeOnly.MinValue);
            week = (ISOWeek.GetYear(day), ISOWeek.GetWeekOfYear(day));
            if (options.Value("week") is { } weekText && ParseWeek(weekText, week.Year, out week) is { } weekError) return weekError;
        }

        var calendar = await CalendarReader.ReadAsync(notes, vault, tag, ct);
        if (tag is not null && calendar.Entries.Count == 0 && calendar.Problems.Count == 0 && !await CarriedAsync([vault], tag, ct))
        {
            return ShellResult.Error($"calendar: {TagFilter.Unknown(tag, VaultPath.Root.Child(vault))}");
        }

        var printer = new Printer(zoneNow, today);
        var lines = new List<string>();
        if (month is var (year, number))
        {
            lines.Add($"{new DateOnly(year, number, 1).ToString("MMMM yyyy", Invariant)} · /{vault}");
            foreach (var shown in CalendarLayout.Month(calendar, year, number, nowUtc, zoneNow))
            {
                lines.Add($"Week {shown.Week} · {Range(shown.Monday, shown.Monday.AddDays(6), "MMM", years: false)}");
                lines.AddRange(shown.Spanning.Select(e => "  " + printer.Spanning(e)));
                foreach (var day in shown.Days.Where(d => d.Entries.Count > 0))
                {
                    lines.Add("  " + DayHeading(day.Day));
                    lines.AddRange(day.Entries.Select(e => "    " + printer.OnDay(e)));
                }

                if (shown.Spanning.Count == 0 && shown.Days.All(d => d.Entries.Count == 0)) lines.Add("  nothing yet");
            }
        }
        else
        {
            var shown = CalendarLayout.Week(calendar, week.Year, week.Week, nowUtc, zoneNow);
            lines.Add($"Week {shown.Week} · {Range(shown.Monday, shown.Monday.AddDays(6), "MMMM", years: true)} · /{vault}");
            if (shown.Spanning.Count > 0)
            {
                lines.Add("Spanning");
                lines.AddRange(shown.Spanning.Select(e => "  " + printer.Spanning(e)));
            }

            foreach (var day in shown.Days)
            {
                lines.Add(DayHeading(day.Day));
                lines.AddRange(day.Entries.Select(e => "  " + printer.OnDay(e)));
            }
        }

        if (calendar.Problems.Count > 0)
        {
            lines.Add($"Problems ({calendar.Problems.Count})");
            lines.AddRange(calendar.Problems.Select(p => $"  {p.Path.Value}: {p.What}"));
            lines.Add("  calendar set <note> --when <value> writes a valid one; edit fixes the rest");
        }

        return ShellResult.Ok(string.Join('\n', lines));
    }

    private async Task<ShellResult> WriteAsync(CalendarService calendar, string verb, IReadOnlyList<string> args, CancellationToken ct)
    {
        var spec = verb switch { "set" => SetSpec, "close" => CloseSpec, _ => [] };
        if (!Options.TryParse(Name, args, spec, out var options, out var error)) return error;
        var operands = options.Operands.Skip(1).ToList();
        var count = verb == "move" ? 2 : 1;
        if (operands.Count < count)
        {
            return ShellResult.Error(verb == "move"
                ? "calendar: move needs a note and its new when, e.g. calendar move /Work/Plan.md 2026-11-20"
                : $"calendar: {verb} needs a note, e.g. calendar {verb} /Work/Plan.md");
        }

        if (operands.Count > count) return ShellResult.Error($"calendar: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, operands[0], out var path, out var pathError)) return pathError!;
        if (path.Segments.Count < 2 || !path.IsNote) return ShellResult.Error($"calendar: {verb} takes a note, e.g. calendar {verb} /Work/Plan.md");

        switch (verb)
        {
            case "move":
                return await calendar.MoveAsync(path.Value, operands[1], ct);
            case "close":
                return await calendar.CloseAsync(path.Value, options.Value("reason"), ct);
            case "reopen":
                return await calendar.ReopenAsync(path.Value, ct);
            case "unset":
                return await calendar.UnsetAsync(path.Value, ct);
        }

        if (options.Has("task") && options.Has("no-task")) return ShellResult.Error("calendar: --task and --no-task can't go together; give one");
        if (options.Has("until") && options.Has("no-until")) return ShellResult.Error("calendar: --until and --no-until can't go together; give one");
        if (!new[] { "when", "until", "no-until", "task", "no-task", "upcoming", "tz", "reason" }.Any(options.Has))
            return ShellResult.Error("calendar: set needs at least one of --when, --until, --no-until, --task, --no-task, --upcoming, --tz, --reason");

        var change = new CalendarChange(
            When: Given(options, "when"),
            Until: options.Has("no-until") ? new Optional<string?>(null) : Given(options, "until"),
            Task: options.Has("task") ? true : options.Has("no-task") ? false : null,
            Upcoming: Given(options, "upcoming"),
            Tz: Given(options, "tz"),
            Reason: Given(options, "reason"));
        return await calendar.SetAsync(path.Value, change, ct);

        static Optional<string?> Given(Options options, string name) => options.Has(name) ? new Optional<string?>(options.Value(name)) : default;
    }

    private async Task<ShellResult> NotificationsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, NotificationsSpec, out var options, out var error)) return error;
        if (options.Operands.Count > 2) return ShellResult.Error($"calendar: too many arguments{CommandArgs.QuoteHint}");
        if (!TryTag(options, out var tag, out var tagError)) return tagError!;

        IReadOnlyList<VaultCalendar> calendars;
        if (options.Operands.Count == 2)
        {
            // A vault narrows it: what a checkpoint holds is one vault's.
            var (vault, vaultError) = await VaultAsync(options.Operands[1], "calendar notifications /Work", ct);
            if (vault is null) return vaultError!;
            calendars = [await CalendarReader.ReadAsync(notes, vault, tag, ct)];
        }
        else calendars = await CalendarReader.ReadAllAsync(notes, tag, ct);

        if (tag is not null && calendars.All(c => c.Entries.Count == 0 && c.Problems.Count == 0) && !await CarriedAsync([.. calendars.Select(c => c.Vault)], tag, ct))
        {
            return ShellResult.Error($"calendar: {TagFilter.Unknown(tag, calendars.Count == 1 && options.Operands.Count == 2 ? VaultPath.Root.Child(calendars[0].Vault) : VaultPath.Root)}");
        }

        var zoneNow = await zone.ZoneAsync(ct);
        var nowUtc = now();
        var printer = new Printer(zoneNow, Zones.ToLocal(nowUtc, zoneNow).Day);
        var on = WhatsOn.Of(calendars, nowUtc, zoneNow);
        var lines = new List<string>();
        if (on.Count == 0) lines.Add("Nothing on.");
        else
        {
            lines.Add($"What's on ({on.Count})");
            lines.AddRange(on.Select(item => "  " + printer.Notification(item)));
        }

        var withProblems = calendars.Where(c => c.Problems.Count > 0).ToList();
        if (withProblems.Count > 0)
        {
            var count = withProblems.Sum(c => c.Problems.Count);
            var each = string.Join(", ", withProblems.Select(c => $"/{c.Vault} {c.Problems.Count}"));
            lines.Add(count == 1
                ? $"Problems: 1 date can't be read ({each}); calendar /{withProblems[0].Vault} lists it"
                : $"Problems: {count} dates can't be read ({each}); calendar /{withProblems[0].Vault} lists them");
        }

        return ShellResult.Ok(string.Join('\n', lines));
    }

    private async Task<(string? Vault, ShellResult? Error)> VaultAsync(string operand, string example, CancellationToken ct)
    {
        if (!CommandArgs.TryParsePath(Name, operand, out var path, out var pathError)) return (null, pathError);
        if (path.Segments.Count != 1) return (null, ShellResult.Error($"calendar: give a vault, e.g. {example}"));
        return (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0])
            ? (path.Segments[0], null)
            : (null, ShellResult.Error($"calendar: {path.Value}: No such file or directory"));
    }

    private static bool TryTag(Options options, out string? tag, out ShellResult? error)
    {
        tag = null;
        error = null;
        if (options.Value("tag") is not { } asked) return true;
        if ((tag = TagFilter.Parse(asked)) is not null) return true;
        error = ShellResult.Error($"calendar: {TagFilter.NotATag(asked)}");
        return false;
    }

    // Only when the calendar came out empty: a misspelt tag says so instead of showing an empty week.
    private async Task<bool> CarriedAsync(IReadOnlyList<string> vaults, string tag, CancellationToken ct)
    {
        foreach (var vault in vaults)
        {
            var all = await notes.ReadNotesUnderAsync(VaultPath.Root.Child(vault), ct);
            if (all.Any(n => !ArchiveFolder.Contains(n.Path) && TagFilter.Carries(n, tag))) return true;
        }

        return false;
    }

    private const string Years = "calendar: a calendar has the years 2 to 9998";

    [GeneratedRegex(@"\A[0-9]{1,2}\z")]
    private static partial Regex WeekNumber();

    [GeneratedRegex(@"\A([0-9]{4})-W([0-9]{2})\z")]
    private static partial Regex WeekForm();

    [GeneratedRegex(@"\A([0-9]{4})-([0-9]{2})\z")]
    private static partial Regex MonthForm();

    // The layout takes real weeks and months only; the years are those a value can be read in (CalendarTime).
    private static ShellResult? ParseWeek(string text, int currentYear, out (int Year, int Week) week)
    {
        week = default;
        int year, number;
        if (WeekNumber().IsMatch(text)) (year, number) = (currentYear, int.Parse(text, Invariant));
        else if (WeekForm().Match(text) is { Success: true } match)
            (year, number) = (int.Parse(match.Groups[1].Value, Invariant), int.Parse(match.Groups[2].Value, Invariant));
        else return ShellResult.Error($"calendar: --week takes a week such as 46 or 2026-W46, not {text}");

        if (year is < 2 or > 9998) return ShellResult.Error(Years);
        var weeks = ISOWeek.GetWeeksInYear(year);
        if (number < 1 || number > weeks) return ShellResult.Error($"calendar: week {number} doesn't exist in {year} (it has {weeks})");
        week = (year, number);
        return null;
    }

    private static ShellResult? ParseMonth(string text, out (int Year, int Month) month)
    {
        month = default;
        var notAMonth = ShellResult.Error($"calendar: --month takes a month such as 2026-11, not {text}");
        if (MonthForm().Match(text) is not { Success: true } match) return notAMonth;
        var year = int.Parse(match.Groups[1].Value, Invariant);
        var number = int.Parse(match.Groups[2].Value, Invariant);
        if (year is < 2 or > 9998) return ShellResult.Error(Years);
        if (number is < 1 or > 12) return notAMonth;
        month = (year, number);
        return null;
    }

    private static string DayHeading(DateOnly day) => day.ToString("ddd d MMM", Invariant);

    /// <summary>"9–15 November 2026", "26 October – 1 November 2026", "28 December 2026 – 3 January 2027"; without the years "9–15 Nov".</summary>
    private static string Range(DateOnly first, DateOnly last, string monthForm, bool years)
    {
        var year = years ? " yyyy" : "";
        if (first.Year == last.Year && first.Month == last.Month) return $"{first.Day}–{last.ToString("d " + monthForm + year, Invariant)}";
        var from = first.ToString("d " + monthForm + (first.Year != last.Year ? year : ""), Invariant);
        return $"{from} – {last.ToString("d " + monthForm + year, Invariant)}";
    }

    /// <summary>The lines of entries, in the caller's zone at one instant.</summary>
    private sealed class Printer(TimeZoneInfo zone, DateOnly today)
    {
        /// <summary>"Tue 3 – Sat 14 Nov  Urlaub  /Work/Urlaub.md  ongoing · until Sat"; a week as "Week 46".</summary>
        public string Spanning(DayEntry item) => Row(SpanOf(item.Entry), item.Entry, item.Status);

        /// <summary>"14:00  Zahnarzt  /Work/Zahnarzt.md", a day without a time without one.</summary>
        public string OnDay(DayEntry item) => Row(item.Entry.When.Kind == TimeKind.Time ? EntryText.Clock(item.Entry, zone) : null, item.Entry, item.Status);

        /// <summary>"overdue · 1 day  Certificate  /Work/Pump/Certificate.md  to do".</summary>
        public string Notification(WhatsOnItem item)
        {
            var kind = Kind(item.Entry);
            return string.Join("  ", [State(item.Status)!, item.Entry.Title, item.Entry.Path.Value, .. kind.Count > 0 ? [string.Join(" · ", kind)] : Array.Empty<string>()]);
        }

        private string Row(string? when, Entry entry, EntryStatus status)
        {
            var details = Kind(entry);
            if (State(status) is { } state) details.Add(state);
            var parts = new List<string>();
            if (when is not null) parts.Add(when);
            parts.Add(entry.Title);
            parts.Add(entry.Path.Value);
            if (details.Count > 0) parts.Add(string.Join(" · ", details));
            return string.Join("  ", parts);
        }

        private static string? State(EntryStatus status) => status.State switch
        {
            EntryState.Upcoming => "upcoming · " + status.Span,
            EntryState.Ongoing => "ongoing · " + status.Span,
            EntryState.Overdue => "overdue · " + status.Span,
            _ => null,
        };

        private List<string> Kind(Entry entry)
        {
            var words = new List<string>();
            if (entry.Kind == EntryKind.Card)
            {
                words.Add("card · " + entry.Column);
                return words;
            }

            if (entry.ByName) words.Add(entry.When.Kind == TimeKind.Week ? "week note" : "day note");
            if (entry.Task == TaskMark.Open) words.Add("to do");
            if (entry.Task == TaskMark.Closed)
            {
                words.Add(entry.Closed is { } closed ? "closed " + closed.ToString(closed.Year == today.Year ? "d MMM" : "d MMM yyyy", Invariant) : "closed");
                if (entry.Reason is { Length: > 0 } reason) words.Add(OneLine(reason));
            }

            return words;
        }

        private string SpanOf(Entry entry)
        {
            var when = entry.When;
            if (when.Kind == TimeKind.Week)
            {
                if (entry.Until is not { } untilWeek) return $"Week {when.IsoWeek}";
                return untilWeek.IsoYear == when.IsoYear ? $"Weeks {when.IsoWeek}–{untilWeek.IsoWeek}" : $"{when.Write()} – {untilWeek.Write()}";
            }

            if (when.Kind == TimeKind.Day)
            {
                var first = when.FirstDay;
                var last = (entry.Until ?? when).LastDay;
                if (first.Year != last.Year) return $"{first.ToString("ddd d MMM yyyy", Invariant)} – {last.ToString("ddd d MMM yyyy", Invariant)}";
                return first.Month == last.Month
                    ? $"{first.ToString("ddd d", Invariant)} – {last.ToString("ddd d MMM", Invariant)}"
                    : $"{first.ToString("ddd d MMM", Invariant)} – {last.ToString("ddd d MMM", Invariant)}";
            }

            var (shownZone, pinned) = EntryText.Shown(entry, zone);
            var (startDay, startClock) = EntryText.Local(when, entry, zone, shownZone);
            var text = $"{startDay.ToString("ddd d MMM", Invariant)} {startClock.ToString("HH:mm", Invariant)}";
            if (entry.Until is { } until)
            {
                var (endDay, endClock) = EntryText.Local(until, entry, zone, shownZone);
                text += $" – {endDay.ToString("ddd d MMM", Invariant)} {endClock.ToString("HH:mm", Invariant)}";
            }

            return text + pinned;
        }

        private static string OneLine(string text) => new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
    }
}
