// SPDX-License-Identifier: AGPL-3.0-or-later
// A date written as Moment writes it (Kumbu.Core Queries/Moment), which Obsidian's format() and relative() are: in
// English, without a zone (bases spec 2026-11-25 decision 14). A moment is "yyyy-MM-ddTHH:mm:ss[.fraction]".

// Longest first, so MMMM is not read as MM twice. Moment's other tokens (Mo, DDD, Q, W, w, E, e, X, x, Z, k, N, gg, GG,
// Y) are left out: their letters stand as written.
const TOKENS = ["YYYY", "YY", "MMMM", "MMM", "MM", "M", "Do", "DD", "D", "dddd", "ddd", "dd", "d", "HH", "H", "hh", "h", "mm", "m", "ss", "s", "SSS", "SS", "S", "A", "a"];
const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

type Parts = { year: number; month: number; day: number; hour: number; minute: number; second: number; millisecond: number; weekday: number };

function parts(moment: string): Parts {
  const [year, month, day] = [Number(moment.slice(0, 4)), Number(moment.slice(5, 7)), Number(moment.slice(8, 10))];
  const at = new Date(0);
  at.setUTCFullYear(year, month - 1, day);
  return {
    year, month, day,
    hour: Number(moment.slice(11, 13)), minute: Number(moment.slice(14, 16)), second: Number(moment.slice(17, 19)),
    millisecond: Number(moment.slice(20, 23).padEnd(3, "0")),
    weekday: at.getUTCDay(),
  };
}

const pad = (n: number, width = 2) => String(n).padStart(width, "0");

function ordinal(day: number): string {
  const suffix = Math.floor((day % 100) / 10) === 1 ? "th" : day % 10 === 1 ? "st" : day % 10 === 2 ? "nd" : day % 10 === 3 ? "rd" : "th";
  return `${day}${suffix}`;
}

function written(p: Parts, token: string): string {
  const hour12 = p.hour % 12 === 0 ? 12 : p.hour % 12;
  switch (token) {
    case "YYYY": return pad(p.year, 4);
    case "YY": return pad(p.year % 100);
    case "MMMM": return MONTHS[p.month - 1]!;
    case "MMM": return MONTHS[p.month - 1]!.slice(0, 3);
    case "MM": return pad(p.month);
    case "M": return String(p.month);
    case "Do": return ordinal(p.day);
    case "DD": return pad(p.day);
    case "D": return String(p.day);
    case "dddd": return DAYS[p.weekday]!;
    case "ddd": return DAYS[p.weekday]!.slice(0, 3);
    case "dd": return DAYS[p.weekday]!.slice(0, 2);
    case "d": return String(p.weekday);
    case "HH": return pad(p.hour);
    case "H": return String(p.hour);
    case "hh": return pad(hour12);
    case "h": return String(hour12);
    case "mm": return pad(p.minute);
    case "m": return String(p.minute);
    case "ss": return pad(p.second);
    case "s": return String(p.second);
    case "SSS": return pad(p.millisecond, 3);
    case "SS": return pad(Math.floor(p.millisecond / 10));
    case "S": return String(Math.floor(p.millisecond / 100));
    case "A": return p.hour < 12 ? "AM" : "PM";
    default: return p.hour < 12 ? "am" : "pm";
  }
}

/** The date in Moment's tokens: YYYY YY M MM MMM MMMM D DD Do d dd ddd dddd H HH h hh m mm s ss S SS SSS A a; `[text]`
 * and `\x` stand as written. Null when the text would grow past `limit` characters. */
export function format(moment: string, pattern: string, limit: number): string | null {
  const p = parts(moment);
  let text = "";
  for (let i = 0; i < pattern.length;) {
    if (text.length > limit) return null;
    // [text] is what stands up to the next ], unless a [ comes first: then this [ is a character of its own. Read forward
    // to whichever comes first, so a pattern of many [ stays linear.
    if (pattern[i] === "[") {
      let close = i + 1;
      while (close < pattern.length && pattern[close] !== "[" && pattern[close] !== "]") close++;
      if (pattern[close] === "]") {
        text += pattern.slice(i + 1, close);
        i = close + 1;
        continue;
      }
    }
    if (pattern[i] === "\\" && i + 1 < pattern.length) {
      text += pattern[i + 1];
      i += 2;
      continue;
    }
    const token = TOKENS.find((t) => pattern.startsWith(t, i));
    if (token === undefined) {
      text += pattern[i++];
      continue;
    }
    text += written(p, token);
    i += token.length;
  }
  return text.length > limit ? null : text;
}

// A moment as milliseconds and the 100 ns ticks below them, read as UTC since it has no zone.
function instant(moment: string): number {
  const p = parts(moment);
  const at = new Date(0);
  at.setUTCFullYear(p.year, p.month - 1, p.day);
  at.setUTCHours(p.hour, p.minute, p.second, 0);
  return at.getTime() + Number(moment.slice(20).padEnd(7, "0")) / 10_000;
}

// .NET's AddMonths: the day clamped to the month's last.
function addMonths(moment: string, months: number): number {
  const p = parts(moment);
  const month = p.year * 12 + p.month - 1 + months;
  const [year, monthOfYear] = [Math.floor(month / 12), ((month % 12) + 12) % 12];
  const last = new Date(0);
  last.setUTCFullYear(year, monthOfYear + 1, 0);
  const at = new Date(0);
  at.setUTCFullYear(year, monthOfYear, Math.min(p.day, last.getUTCDate()));
  at.setUTCHours(p.hour, p.minute, p.second, 0);
  return at.getTime() + Number(moment.slice(20).padEnd(7, "0")) / 10_000;
}

// JavaScript's Math.round, which Moment rounds with; written as floor(x + 0.5), as the server's engine rounds.
const round = (n: number) => Math.floor(n + 0.5);

/** How far the date is from now, as Moment's fromNow() says it: the calendar months between them and the rest in
 * milliseconds, rounded at Moment's thresholds ("a few seconds ago", "in 3 hours", "2 months ago"). */
export function fromNow(date: string, now: string): string {
  const future = instant(date) > instant(now);
  const [from, to] = future ? [now, date] : [date, now];
  const [a, b] = [parts(from), parts(to)];
  let months = b.month - a.month + (b.year - a.year) * 12;
  if (addMonths(from, months) > instant(to)) months--;
  const milliseconds = instant(to) - addMonths(from, months);

  // Moment's Duration.as(): days from months on Moment's average month for the small units, months from days for the large.
  const days = round((months * 146097) / 4800);
  const seconds = round(days * 86400 + milliseconds / 1000);
  const minutes = round(days * 1440 + milliseconds / 60_000);
  const hours = round(days * 24 + milliseconds / 3_600_000);
  const wholeDays = round(days + milliseconds / 86_400_000);
  const inMonths = months + ((milliseconds / 86_400_000) * 4800) / 146097;
  const [monthsRounded, years] = [round(inMonths), round(inMonths / 12)];

  const said = seconds <= 44 ? "a few seconds"
    : minutes <= 1 ? "a minute"
    : minutes < 45 ? `${minutes} minutes`
    : hours <= 1 ? "an hour"
    : hours < 22 ? `${hours} hours`
    : wholeDays <= 1 ? "a day"
    : wholeDays < 26 ? `${wholeDays} days`
    : monthsRounded <= 1 ? "a month"
    : monthsRounded < 11 ? `${monthsRounded} months`
    : years <= 1 ? "a year"
    : `${years} years`;
  return future ? `in ${said}` : `${said} ago`;
}
