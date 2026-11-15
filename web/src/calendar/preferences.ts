// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { api, call } from "../api/client";
import { useSession } from "../state/session";

/** How a weekday is for someone: shaded in the calendar when lighter or off. */
export type DayLevel = "workday" | "lighter" | "off";

/**
 * `GET /api/account/calendar`: the person's zone, null until one is stored (the calendar then uses another), and the
 * person's week, Monday first, null or absent until one is chosen.
 */
export type CalendarPreferences = { zone: string | null; serverZone: string; days?: DayLevel[] | null };

type Answer<T> = Promise<{ data?: T; error?: unknown; response: Response }>;

/** As in `api.ts`: the app's client, its middleware and `call`'s errors, without the types the schema doesn't have yet. */
const untyped = api as unknown as { GET<T>(url: string): Answer<T>; PUT<T>(url: string, init: { body: unknown }): Answer<T> };

const queryKey = ["calendar-preferences"];

export const useCalendarPreferences = () =>
  useQuery({ queryKey, queryFn: () => call(untyped.GET<CalendarPreferences>("/api/account/calendar")), staleTime: 60_000 });

const putZone = (zone: string | null) => call(untyped.PUT<CalendarPreferences>("/api/account/calendar", { body: { zone } }));

/** A new zone moves every time the calendar shows: its views are asked again. */
function zoneChanged(queryClient: QueryClient, view: CalendarPreferences) {
  queryClient.setQueryData(queryKey, view);
  for (const key of [["calendar"], ["whats-on"], ["note-entry"]]) void queryClient.invalidateQueries({ queryKey: key });
}

export function useSetZone() {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: putZone, onSuccess: (view) => zoneChanged(queryClient, view) });
}

const putDays = (days: DayLevel[] | null) => call(untyped.PUT<CalendarPreferences>("/api/account/calendar", { body: { days } }));

/** The week is shaded by the client: no calendar view is asked again. */
export function useSetDays() {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: putDays, onSuccess: (view) => queryClient.setQueryData(queryKey, view) });
}

type WeekInfo = { weekend?: number[] };

/** The browser locale's weekend as ISO weekdays (7 is Sunday); Saturday and Sunday where it can't say. */
export function browserWeekend(language = navigator.language): number[] {
  try {
    const locale = new Intl.Locale(language) as Intl.Locale & { getWeekInfo?(): WeekInfo; weekInfo?: WeekInfo };
    const weekend = (locale.getWeekInfo?.() ?? locale.weekInfo)?.weekend;
    if (weekend?.length) return weekend;
  } catch {
    // An odd language tag: the default below.
  }
  return [6, 7];
}

/** Until someone chooses: the weekend off, no lighter days. Monday first. */
export function defaultDays(language?: string): DayLevel[] {
  const weekend = browserWeekend(language);
  return [1, 2, 3, 4, 5, 6, 7].map((d) => (weekend.includes(d) ? "off" : "workday"));
}

/** The person's week: as chosen, or the browser's weekend. */
export function useDays(): DayLevel[] {
  const days = useCalendarPreferences().data?.days;
  return days ?? defaultDays();
}

/** Every IANA zone the browser knows, or null where it can't list them. */
export function knownZones(): string[] | null {
  try {
    return Intl.supportedValuesOf("timeZone");
  } catch {
    return null;
  }
}

export const browserZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;

/** Whose zone this page has preset already (or tried to): once per user and page load. */
let presetFor: string | null = null;

/** For tests: the next session presets afresh. */
export function forgetZonePreset() {
  presetFor = null;
}

/**
 * The first time the web app runs for someone with no zone stored, the browser's becomes theirs. A failure says
 * nothing: the next session tries again.
 */
export function useZonePreset() {
  const userId = useSession((s) => s.user?.id ?? null);
  const preferences = useCalendarPreferences();
  const queryClient = useQueryClient();
  const zone = preferences.data?.zone;

  useEffect(() => {
    if (!userId || zone !== null || presetFor === userId) return;
    presetFor = userId;
    const browser = browserZone();
    if (!browser) return;
    putZone(browser).then(
      (view) => zoneChanged(queryClient, view),
      () => {},
    );
  }, [userId, zone, queryClient]);
}
