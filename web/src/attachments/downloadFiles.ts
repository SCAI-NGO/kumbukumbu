// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { fileUrl, inVault, vaultOf } from "../api/paths";
import { browserZone, download } from "../data/exportUrl";

/** The server takes at most this many files in one ZIP. */
export const maxZipFiles = 100;

/** One file's download as it is: never needs the password. */
export const downloadFile = (path: string) => download(`${fileUrl(vaultOf(path), inVault(path))}?download=1`);

/**
 * Several attachments of a vault (paths from the root of the tree) as one ZIP: a form posted to
 * `POST /api/files/{vault}/zip`, since a hundred paths would make a GET's address too long. The browser downloads the
 * answer as a link's would (the session cookie goes along, the ZIP streams to disk, the page stays), and sends its
 * Origin, which the server checks. `ticket` comes from the password when the user's setting asks for it.
 */
export function postZip(vault: string, paths: string[], ticket?: string, tz: string = browserZone()) {
  const form = document.createElement("form");
  form.method = "POST";
  form.action = `/api/files/${encodeURIComponent(vault)}/zip`;
  form.hidden = true;
  const field = (name: string, value: string) => {
    const input = document.createElement("input");
    input.type = "hidden";
    input.name = name;
    input.value = value;
    form.append(input);
  };
  for (const path of paths) field("path", inVault(path));
  field("tz", tz);
  if (ticket !== undefined) field("ticket", ticket);
  document.body.append(form);
  form.submit();
  form.remove();
}

/** Several files as a ZIP; with `password`, checked for a one-time ticket first (as an export's). */
export async function downloadZip(vault: string, paths: string[], password?: string) {
  if (password === undefined) return postZip(vault, paths);
  const { ticket } = await call(api.POST("/api/export/ticket", { body: { password } }));
  postZip(vault, paths, ticket);
}

/** The account's preferences (`GET /api/account/preferences`): whether several files need the password. */
export const preferencesQuery = { queryKey: ["preferences"], queryFn: () => call(api.GET("/api/account/preferences")) };

export const usePreferences = () => useQuery(preferencesQuery);
