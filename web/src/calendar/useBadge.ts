// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useEffect, useMemo } from "react";
import { useSession } from "../state/session";
import { badgeQuery } from "./api";

/** A count of What's on and whether anything in it is overdue: all of it, or one vault's share. */
export type Share = { count: number; overdue: boolean };
export type BadgeCount = Share & { vaults: Map<string, Share> };

/** The installed app's icon carries the count where the browser has the Badging API; elsewhere nothing happens. */
export function setAppBadge(count: number) {
  if (!("setAppBadge" in navigator)) return;
  navigator.setAppBadge(count).catch(() => {});
}

export function clearAppBadge() {
  if (!("clearAppBadge" in navigator)) return;
  navigator.clearAppBadge().catch(() => {});
}

// A sign-out, a session that ended and another user's session take the count off the icon: it was the last one's.
useSession.subscribe((session, previous) => {
  if (previous.user !== null && session.user?.id !== previous.user.id) clearAppBadge();
});

const none: Share = { count: 0, overdue: false };

/**
 * The badge (spec §7.7): the length of What's on over the vaults the session shows, the share of each, and whether
 * anything is overdue. Asked when the app opens, again whenever it comes back to the foreground (TanStack's
 * `visibilitychange`) and after a change to an entry (`["whats-on"]` is invalidated); the app's icon follows it.
 */
export function useBadge(): BadgeCount {
  const { data } = useQuery({ ...badgeQuery, staleTime: 30_000, refetchOnWindowFocus: "always" });
  const count = data?.count;
  useEffect(() => {
    if (count === undefined) return;
    if (count > 0) setAppBadge(count);
    else clearAppBadge();
  }, [count]);
  return useMemo(
    () =>
      data
        ? { count: data.count, overdue: data.overdue, vaults: new Map(data.vaults.map((v) => [v.vault, { count: v.count, overdue: v.overdue }])) }
        : { ...none, vaults: new Map() },
    [data],
  );
}
