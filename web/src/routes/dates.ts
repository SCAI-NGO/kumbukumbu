// SPDX-License-Identifier: AGPL-3.0-or-later

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });

/** A credential's day, as the access lists show it: "1 Oct 2026". */
export const date = (iso: string) => day.format(new Date(iso));

/** Whether a credential's expiry has come; never for one that doesn't expire. */
export const isExpired = (item: { expiresAt: string | null }) => item.expiresAt !== null && new Date(item.expiresAt).getTime() <= Date.now();

/** " · Expires …", " · Expired …" or " · Doesn't expire"; nothing for a revoked one, whose expiry no longer matters. */
export function expiryText(item: { revokedAt: string | null; expiresAt: string | null }) {
  if (item.revokedAt !== null) return "";
  if (!item.expiresAt) return " · Doesn't expire";
  return ` · ${isExpired(item) ? "Expired" : "Expires"} ${date(item.expiresAt)}`;
}
