// SPDX-License-Identifier: AGPL-3.0-or-later

const pad = (n: number) => String(n).padStart(2, "0");

/** The formats a photo is kept in, with the extension its name gets; the upload shrinks a large one to JPEG or WebP. */
const extensions: Record<string, string> = { "image/jpeg": "jpg", "image/png": "png", "image/gif": "gif", "image/webp": "webp" };

/**
 * The extension a photo of media type `type` is stored with; null for any other format (HEIC, say), which keeps the
 * camera's own name and is refused by the upload with its usual sentence.
 */
export const extOf = (type: string): string | null => extensions[type] ?? null;

/**
 * "Photo 2026-11-14 093005.jpg" in local time, the archive's stamp; " 2", " 3" while the name is taken in the folder
 * (`taken`: its file names, compared ignoring case as the vault does).
 */
export function photoName(now: Date, ext: string, taken: Set<string>): string {
  const stem = `Photo ${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
  const lower = new Set([...taken].map((name) => name.toLowerCase()));
  for (let n = 1; ; n++) {
    const name = `${stem}${n === 1 ? "" : ` ${n}`}.${ext}`;
    if (!lower.has(name.toLowerCase())) return name;
  }
}
