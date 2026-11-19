// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { fillPlaceholders, formatMoment } from "./templates";

// Thursday 2026-11-19, 14:05:09 in Vienna (UTC+1).
const thursday = new Date("2026-11-19T13:05:09Z");
const at = { title: "Weekly", now: thursday, zone: "Europe/Vienna" };

describe("fillPlaceholders", () => {
  it("puts in the note's title, the date and the time", () => {
    expect(fillPlaceholders("# {{title}}\n{{date}} {{time}}", at)).toBe("# Weekly\n2026-11-19 14:05");
  });

  it("takes a Moment format after the colon", () => {
    expect(fillPlaceholders("{{date:dddd, D MMMM YYYY}} at {{time:HH:mm:ss}}", at)).toBe("Thursday, 19 November 2026 at 14:05:09");
  });

  it("goes by the zone it is given: near midnight it is another day elsewhere", () => {
    const late = new Date("2026-11-19T23:30:00Z");
    expect(fillPlaceholders("{{date}} {{time}}", { ...at, now: late, zone: "Europe/Vienna" })).toBe("2026-11-20 00:30");
    expect(fillPlaceholders("{{date}} {{time}}", { ...at, now: late, zone: "America/New_York" })).toBe("2026-11-19 18:30");
    expect(fillPlaceholders("{{date:ddd}}", { ...at, now: late, zone: "Pacific/Kiritimati" })).toBe("Fri");
  });

  it("falls back to the browser's zone for one it doesn't know", () => {
    const browser = fillPlaceholders("{{date}} {{time}}", { ...at, zone: Intl.DateTimeFormat().resolvedOptions().timeZone });
    expect(fillPlaceholders("{{date}} {{time}}", { ...at, zone: "Nowhere/Atlantis" })).toBe(browser);
  });

  it.each([
    ["{{other}}"],
    ["{{title:upper}}"],
    ["{{ date }}"],
    ["{{Date}}"],
    ["<% tp.date.now() %>"],
    ["<%* tR += tp.file.title %>"],
    ["{{date"],
  ])("leaves %s as written", (text) => {
    expect(fillPlaceholders(text, at)).toBe(text);
  });

  it("fills every placeholder of a text, also several on a line", () => {
    expect(fillPlaceholders("{{date:YYYY}}-{{date:MM}} {{title}} {{title}}", at)).toBe("2026-11 Weekly Weekly");
  });
});

describe("formatMoment", () => {
  // Saturday 2026-03-07, 09:04:03 in Vienna: single digits show what M, D and H leave out.
  const saturday = new Date("2026-03-07T08:04:03Z");
  it.each([
    ["YYYY", "2026"],
    ["YY", "26"],
    ["MMMM", "March"],
    ["MMM", "Mar"],
    ["MM", "03"],
    ["M", "3"],
    ["DD", "07"],
    ["D", "7"],
    ["dddd", "Saturday"],
    ["ddd", "Sat"],
    ["HH", "09"],
    ["H", "9"],
    ["mm", "04"],
    ["ss", "03"],
  ])("%s gives %s", (format, expected) => {
    expect(formatMoment(saturday, "Europe/Vienna", format)).toBe(expected);
  });

  it("reads the longest token first, and leaves the text between them", () => {
    expect(formatMoment(saturday, "Europe/Vienna", "MMMM/MMM/MM/M")).toBe("March/Mar/03/3");
    expect(formatMoment(saturday, "Europe/Vienna", "YYYYMMDD-HHmm")).toBe("20260307-0904");
  });

  it("keeps what is in square brackets as written, as Moment does", () => {
    expect(formatMoment(saturday, "Europe/Vienna", "[Week of] D MMM [at] HH[h]")).toBe("Week of 7 Mar at 09h");
  });

  it("shows midnight as 00, not 24", () => {
    expect(formatMoment(new Date("2026-03-06T23:00:00Z"), "Europe/Vienna", "HH:mm H")).toBe("00:00 0");
  });

  it("names days and months in English whatever the browser's language", () => {
    expect(formatMoment(new Date("2026-09-01T10:00:00Z"), "Europe/Vienna", "ddd dddd MMM MMMM")).toBe("Tue Tuesday Sep September");
  });
});
