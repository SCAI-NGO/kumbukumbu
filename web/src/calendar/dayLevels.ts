// SPDX-License-Identifier: AGPL-3.0-or-later
import { isoWeekday } from "./dates";
import type { DayLevel } from "./preferences";

/** What a day is in the person's week. */
export const dayLevelOf = (days: DayLevel[], day: string) => days[isoWeekday(day) - 1];

/** A day off shaded, a lighter day lightly: the page mixed towards its muted text, so it shows in every scheme. */
export const dayShade: Record<DayLevel, string> = {
  workday: "",
  lighter: "bg-[color-mix(in_srgb,var(--muted)_8%,var(--bg))]",
  off: "bg-[color-mix(in_srgb,var(--muted)_18%,var(--bg))]",
};

/** The shading in words, for a screen reader: colour alone says nothing to it. */
export const dayWord: Record<DayLevel, string | null> = { workday: null, lighter: "Lighter day", off: "Day off" };
