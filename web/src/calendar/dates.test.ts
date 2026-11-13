// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { addDays, dayHeading, dayLabel, formatWeekTitle, weekId, weekOf } from "./dates";

describe("calendar dates", () => {
  it("titles a week within one month", () => {
    expect(formatWeekTitle({ week: 46, monday: "2026-11-09" })).toBe("Week 46 · 9–15 November 2026");
  });

  it("titles a week across two months", () => {
    expect(formatWeekTitle({ week: 44, monday: "2026-10-26" })).toBe("Week 44 · 26 October – 1 November 2026");
  });

  it("titles a week across two years", () => {
    expect(formatWeekTitle({ week: 1, monday: "2025-12-29" })).toBe("Week 1 · 29 December 2025 – 4 January 2026");
  });

  it("names a day as a heading", () => {
    expect(dayHeading("2026-11-12")).toBe("Thursday, 12 November");
    expect(dayHeading("2026-03-29")).toBe("Sunday, 29 March");
  });

  it("reads a day out with its entries and how many are overdue", () => {
    const e = (state: string) => ({ state });
    expect(dayLabel("2026-11-12", [e("overdue"), e("upcoming"), e("notYet")])).toBe("Thursday, 12 November, 3 entries, 1 overdue");
    expect(dayLabel("2026-11-12", [e("ongoing")])).toBe("Thursday, 12 November, 1 entry");
    expect(dayLabel("2026-11-12", [])).toBe("Thursday, 12 November, no entries");
  });

  it("counts days and ISO weeks from the date's parts, whatever the local zone", () => {
    expect(addDays("2026-11-09", -7)).toBe("2026-11-02");
    expect(addDays("2026-12-28", 7)).toBe("2027-01-04");
    expect(addDays("2026-03-28", 1)).toBe("2026-03-29");
    expect(weekOf("2026-11-12")).toEqual({ year: 2026, week: 46 });
    expect(weekOf("2025-12-29")).toEqual({ year: 2026, week: 1 });
    expect(weekOf("2027-01-03")).toEqual({ year: 2026, week: 53 });
    expect(weekId(weekOf("2026-01-05"))).toBe("2026-W02");
  });
});
