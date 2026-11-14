// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { api, call } from "../api/client";
import { useSession } from "../state/session";

/** `GET /api/account/calendar`: the person's zone, null until one is stored (the calendar then uses another). */
export type CalendarPreferences = { zone: string | null; serverZone: string };

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
