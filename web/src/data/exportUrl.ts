// SPDX-License-Identifier: AGPL-3.0-or-later

/** The browser's IANA time zone (e.g. `Europe/Vienna`), so the ZIP's entry times read right where it is unpacked. */
export const browserZone = (): string => Intl.DateTimeFormat().resolvedOptions().timeZone ?? "UTC";

/**
 * The server's export of these vaults: the vault's own route for one, `/api/export?vault=…` for several. `tz` is the
 * reader's time zone (ZIP entry times have none and are read as local time); the server falls back to UTC.
 */
export function exportUrl(vaults: string[], tz: string = browserZone()): string {
  const zone = `tz=${encodeURIComponent(tz)}`;
  if (vaults.length === 1) return `/api/vaults/${encodeURIComponent(vaults[0])}/export?${zone}`;
  return `/api/export?${vaults.map((v) => `vault=${encodeURIComponent(v)}`).join("&")}&${zone}`;
}

/**
 * Starts the browser's download of a same-origin `GET`: the session cookie goes along, the server names the file,
 * and the ZIP streams to disk without passing through the page's memory (no fetch, no blob).
 */
export function download(href: string) {
  const link = document.createElement("a");
  link.href = href;
  link.download = "";
  link.hidden = true;
  document.body.append(link);
  link.click();
  link.remove();
}
