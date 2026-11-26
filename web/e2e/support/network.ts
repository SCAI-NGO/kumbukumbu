// SPDX-License-Identifier: AGPL-3.0-or-later
// A network as it really comes back. Playwright's `setOffline(false)` gives a working network at once; Wi-Fi doesn't:
// the browser says "online" as soon as it joins, while DHCP, DNS and TLS still take seconds, and requests fail
// meanwhile. What an app does in that gap is what these helpers let a test see.
import type { BrowserContext, Route, WebSocketRoute } from "@playwright/test";

/**
 * Brings the context back online the way Wi-Fi does: the browser is online at once (its `online` event fires), but
 * every request to the API fails as without a network for `ms` more. Resolves once the gap is over and the routing is
 * gone again (in Firefox a page loaded while a route is on bypasses the service worker, so it never stays longer).
 * `which` narrows the failing requests: on a real network change Chromium aborts some requests
 * (`ERR_NETWORK_CHANGED`) while others already go through.
 */
export async function reconnectSlowly(context: BrowserContext, ms: number, which = "**/api/**"): Promise<void> {
  const until = Date.now() + ms;
  const refuse = (route: Route) => (Date.now() < until ? route.abort("internetdisconnected") : route.fallback());
  await context.route(which, refuse);
  await context.setOffline(false);
  // Playwright's emulation doesn't always fire the window's `online` event, which a real network change does and
  // which React Query answers by reading every stale query again.
  for (const page of context.pages()) await page.evaluate(() => window.dispatchEvent(new Event("online"))).catch(() => {});
  await new Promise((resolve) => setTimeout(resolve, ms));
  await context.unroute(which, refuse);
}

/**
 * The network as Chromium on Linux loses it when Wi-Fi goes (measured on Chris's laptop, 2026-11-26): no `offline`
 * event, `navigator.onLine` stays true; requests fail, and the open socket is dropped (Chromium resets its connections
 * on a network change). The app can only notice by asking. Set up before the page opens its socket: the socket goes
 * through Playwright from then on, so `lose` can drop it. `restore` is as quiet: requests and new sockets go through
 * again, with no event either.
 */
export async function quietNetwork(context: BrowserContext): Promise<{ lose: () => Promise<void>; restore: () => Promise<void> }> {
  let lost = false;
  const open = new Set<WebSocketRoute>();
  await context.routeWebSocket("**/ws**", (ws) => {
    if (lost) return void ws.close({ code: 1006 }).catch(() => {});
    ws.connectToServer();
    open.add(ws);
    ws.onClose(() => open.delete(ws));
  });
  const refuse = (route: Route) => route.abort("internetdisconnected");
  return {
    lose: async () => {
      lost = true;
      await context.route("**/api/**", refuse);
      for (const ws of [...open]) await ws.close({ code: 1006 }).catch(() => {});
    },
    restore: async () => {
      lost = false;
      await context.unroute("**/api/**", refuse);
    },
  };
}
