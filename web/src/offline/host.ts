// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode's wiring (offline mode design §3, §4.8 to §4.11): the mode, the copy this tab holds, the reading
// source, the offline writes and the sync engines, tied together once at the app's start. Each part was built on its
// own behind these seams; this is the one place that knows all of them.
import type { QueryClient } from "@tanstack/react-query";
import type { SealedMirror } from "eidolon-core/store/mirror";
import { api, call } from "../api/client";
import { useSession } from "../state/session";
import { useOfflineCopy } from "./copy";
import { enginesLocalChanged, listenToEngines } from "./engines";
import { mirrorCopies, onBeforeLock, onContacted, useOfflineMode } from "./mode";
import { nameVaults, OfflineSource, setOfflineSource } from "./source";
import { flushOfflineEditors, onOfflineWrite, setOfflineWrites } from "./writing";

/** What reads through the offline source: asked again on every switch, so what is shown comes from the right side. */
const READ_AGAIN = ["vaults", "tree", "note", "links", "backlinks", "version", "search"];

let reading: SealedMirror | null = null;

/** Reads and writes go to the copy while the app is offline (or still uploading) with the copy unlocked here. */
function follow(queryClient: QueryClient): void {
  const { mirror } = useOfflineCopy.getState();
  const on = useOfflineMode.getState().offline && mirror !== null && !mirror.locked ? mirror : null;
  if (on === reading) return;
  reading = on;
  if (on === null) {
    setOfflineSource(null);
    setOfflineWrites(null);
  } else {
    const source = new OfflineSource(on);
    setOfflineSource(source);
    setOfflineWrites({ vaultFor: (path) => source.vaultFor(path), written: enginesLocalChanged });
  }
  for (const key of READ_AGAIN) void queryClient.invalidateQueries({ queryKey: [key] });
}

/** Whether this device keeps any copy: offline after a reload, the bar says so before the sign-in form opens it. */
function lookForCopies(): void {
  if (typeof indexedDB === "undefined") return;
  void mirrorCopies()
    .then((copies) => copies.headers())
    .then((headers) => useOfflineCopy.setState({ stored: headers.length > 0 || useOfflineCopy.getState().header !== null }))
    .catch(() => {});
}

/** Once, at the app's start. Returns a function that undoes it, for the tests. */
export function runOfflineHost(queryClient: QueryClient): () => void {
  const stops = [
    useOfflineMode.subscribe(() => follow(queryClient)),
    useOfflineCopy.subscribe((s, before) => {
      follow(queryClient);
      // Locked: what the app read from it (or online, in the session it stood for) goes from memory too. Removed
      // while an online session goes on in this tab (Settings, Offline): what it read is that session's, and stays.
      const online = !!useSession.getState().user?.id && !useOfflineMode.getState().offline;
      if (s.mirror === null && before.mirror !== null && !online) queryClient.clear();
      // Removed or wiped: another user's copy may still be here.
      if (s.header === null && before.header !== null) lookForCopies();
    }),
    listenToEngines({
      conflict: (vault, original, copy, came) => useOfflineMode.getState().conflict({ vault, original, copy, ...(came === undefined ? {} : { came }) }),
      sessionLost: () => useOfflineMode.getState().sessionLost(),
      waitingChanged: () => useOfflineMode.getState().waitingChanged(),
    }),
    onOfflineWrite(() => useOfflineMode.getState().waitingChanged()),
    // An offline unlock ends like a session: what the editors hold unsaved goes into the copy first (§3a).
    onBeforeLock(flushOfflineEditors),
    // Offline, paths name vaults by the name the vault list last gave (§4.8).
    onContacted((mirror) => void call(api.GET("/api/vaults")).then((vaults) => nameVaults(mirror, vaults)).catch(() => {})),
  ];
  follow(queryClient);
  lookForCopies();
  return () => {
    for (const stop of stops) stop();
    reading = null;
  };
}
