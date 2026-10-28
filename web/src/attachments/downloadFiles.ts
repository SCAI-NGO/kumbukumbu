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
 * `POST /api/files/{vault}/zip`, since a hundred paths would make a GET's address too long, into the frame named
 * `target` (`ZipFrame`). The browser downloads the answer as a link's would (the session cookie goes along, the ZIP
 * streams to disk) and sends its Origin, which the server checks; an error loads into the frame, where it is read,
 * instead of replacing the app. `ticket` comes from the password when the user's setting asks for it.
 */
export function postZip(target: string, vault: string, paths: string[], ticket?: string, tz: string = browserZone()) {
  const form = document.createElement("form");
  form.method = "POST";
  form.action = `/api/files/${encodeURIComponent(vault)}/zip`;
  form.target = target;
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

/** Several files as a ZIP into the frame `target`; with `password`, checked for a one-time ticket first (as an export's). */
export async function downloadZip(target: string, vault: string, paths: string[], password?: string) {
  if (password === undefined) return postZip(target, vault, paths);
  const { ticket } = await call(api.POST("/api/export/ticket", { body: { password } }));
  postZip(target, vault, paths, ticket);
}

/**
 * What a ZIP's frame holds after it loaded: null for nothing (an empty page, the frame reset, or nothing loaded
 * because the ZIP went to disk), else the server's sentence, or a general one when it can't be read.
 */
export function zipFrameError(frame: HTMLIFrameElement): string | null {
  let text: string;
  try {
    const document = frame.contentDocument;
    if (!document) return "The download didn't start. Try again.";
    text = document.body?.textContent?.trim() ?? "";
  } catch {
    return "The download didn't start. Try again.";
  }
  if (!text) return null;
  try {
    const { error } = JSON.parse(text) as { error?: unknown };
    if (typeof error === "string" && error) return error;
  } catch {
    // Not the API's JSON (a proxy's page, say).
  }
  return "The download didn't start. Try again.";
}

/** The account's preferences (`GET /api/account/preferences`): whether several files need the password. */
export const preferencesQuery = { queryKey: ["preferences"], queryFn: () => call(api.GET("/api/account/preferences")) };

/** For showing the setting; a download asks the server afresh (`fetchQuery` with `staleTime: 0`). */
export const usePreferences = () => useQuery({ ...preferencesQuery, staleTime: 60_000 });
