// SPDX-License-Identifier: AGPL-3.0-or-later
// The heartbeat: on real Wi-Fi a network can go without the browser saying so (no `offline` event, `onLine` stays
// true), so while the tab is visible the app asks the server every 15 seconds and at once when the connection changes.
// Its answer, or none, is one request for the offline mode like any other (offline mode design §3a).
import { useSyncStatus } from "../state/sync";
import { useOfflineMode } from "./mode";

const EVERY_MS = 15_000;
const WAIT_MS = 5_000;

/** Gateways answer these while Kumbu² behind them is down (as the API client counts them). */
const unreachable = new Set([502, 503, 504]);

/**
 * No answer: the socket is closed as the `offline` event closes it, as it may not hear of the loss for minutes, and
 * the mode switches only once it is down.
 */
function missed() {
  useSyncStatus.getState().drop();
  useOfflineMode.getState().failed();
}

export function startHeartbeat(): () => void {
  let timer: ReturnType<typeof setInterval> | undefined;
  let asking = false;

  async function ask() {
    if (asking || document.visibilityState !== "visible") return;
    asking = true;
    const abort = new AbortController();
    const late = setTimeout(() => abort.abort(), WAIT_MS);
    try {
      // Anonymous and without the cookie or the copy's token: it renews no session and carries nothing secret.
      const response = await fetch("/api/version", { credentials: "omit", cache: "no-store", signal: abort.signal });
      if (unreachable.has(response.status)) missed();
      else useOfflineMode.getState().reached();
    } catch {
      missed();
    } finally {
      clearTimeout(late);
      asking = false;
    }
  }

  function beat() {
    clearInterval(timer);
    timer = document.visibilityState === "visible" ? setInterval(() => void ask(), EVERY_MS) : undefined;
  }

  // A hidden tab isn't woken: it asks again, at once, when it shows.
  function visibility() {
    beat();
    void ask();
  }
  const changed = () => void ask();
  const connection = (navigator as { connection?: EventTarget }).connection;

  beat();
  document.addEventListener("visibilitychange", visibility);
  connection?.addEventListener("change", changed);
  return () => {
    clearInterval(timer);
    document.removeEventListener("visibilitychange", visibility);
    connection?.removeEventListener("change", changed);
  };
}
