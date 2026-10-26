// SPDX-License-Identifier: AGPL-3.0-or-later

/** The server's export of these vaults: the vault's own route for one, `/api/export?vault=…` for several. */
export function exportUrl(vaults: string[]): string {
  if (vaults.length === 1) return `/api/vaults/${encodeURIComponent(vaults[0])}/export`;
  return `/api/export?${vaults.map((v) => `vault=${encodeURIComponent(v)}`).join("&")}`;
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
