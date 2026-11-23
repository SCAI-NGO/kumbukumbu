// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode's wiring (offline mode design §3, §4.8 to §4.11): the mode, the copy this tab holds, the reading
// source, the offline writes and the sync engines, tied together once at the app's start. Each part was built on its
// own behind these seams; this is the one place that knows all of them.
import type { QueryClient } from "@tanstack/react-query";
import type { SealedMirror } from "eidolon-core/store/mirror";
import { api, call } from "../api/client";
import { useOfflineCopy } from "./copy";
import { enginesLocalChanged, listenToEngines } from "./engines";
import { onBeforeLock, onContacted, useOfflineMode } from "./mode";
import { nameVaults, OfflineSource, setOfflineSource } from "./source";
import { flushOfflineEditors, onOfflineWrite, setOfflineWrites } from "./writing";

/** What reads through the offline source: asked again on every switch, so what is shown comes from the right side. */
const READ_AGAIN = ["vaults", "tree", "note", "links", "backlinks", "version"];

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

/** Once, at the app's start. Returns a function that undoes it, for the tests. */
export function runOfflineHost(queryClient: QueryClient): () => void {
  const stops = [
    useOfflineMode.subscribe(() => follow(queryClient)),
    useOfflineCopy.subscribe(() => follow(queryClient)),
    listenToEngines({
      conflict: (vault, original, copy) => useOfflineMode.getState().conflict({ vault, original, copy }),
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
  return () => {
    for (const stop of stops) stop();
    reading = null;
  };
}
