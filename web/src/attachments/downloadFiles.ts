// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { fileUrl, inVault, vaultOf } from "../api/paths";
import { browserZone, download, withTicket } from "../data/exportUrl";

/** The server takes at most this many files in one ZIP. */
export const maxZipFiles = 100;

/** The server's ZIP of these attachments of one vault (paths from the root of the tree). Session only. */
export const zipUrl = (vault: string, paths: string[], tz: string = browserZone()) =>
  `/api/files/${encodeURIComponent(vault)}/zip?${paths.map((p) => `path=${encodeURIComponent(inVault(p))}`).join("&")}&tz=${encodeURIComponent(tz)}`;

/** One file's download as it is: never needs the password. */
export const downloadFile = (path: string) => download(`${fileUrl(vaultOf(path), inVault(path))}?download=1`);

/**
 * Several files of a vault as one ZIP. With `password` it is checked for a one-time ticket first (as an export's):
 * the user's setting asks for it unless they turned it off; without, the ZIP is asked for as it is.
 */
export async function downloadZip(vault: string, paths: string[], password?: string) {
  const href = zipUrl(vault, paths);
  if (password === undefined) return download(href);
  const { ticket } = await call(api.POST("/api/export/ticket", { body: { password } }));
  download(withTicket(href, ticket));
}

/** The account's preferences (`GET /api/account/preferences`): whether several files need the password. */
export const usePreferences = () =>
  useQuery({ queryKey: ["preferences"], queryFn: () => call(api.GET("/api/account/preferences")), staleTime: 60_000 });

