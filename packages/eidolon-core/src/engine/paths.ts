// SPDX-License-Identifier: AGPL-3.0-or-later
// Paths between the Obsidian vault and the server, and the names derived from them.
import { versionOfSync } from "./version";

/** "Besprechung März.md" → "/Work/Besprechung März.md", NFC (macOS and some Android file systems hand out NFD). */
export function toApiPath(vault: string, path: string): string {
  return `/${vault}/${path}`.normalize("NFC");
}

export function fromApiPath(vault: string, apiPath: string): string {
  const prefix = `/${vault}/`;
  const nfc = apiPath.normalize("NFC");
  const p = prefix.normalize("NFC");
  return nfc.startsWith(p) ? nfc.slice(p.length) : nfc.replace(/^\/+/, "");
}

/** The note's REST URL path, each segment encoded like the web app's noteUrl. */
export function urlFor(vault: string, path: string): string {
  return apiUrl("notes", vault, path);
}

/** The attachment's REST URL path (GET/PUT/DELETE /api/files), encoded as a note's. */
export function fileUrlFor(vault: string, path: string): string {
  return apiUrl("files", vault, path);
}

function apiUrl(api: "notes" | "files", vault: string, path: string): string {
  const segments = [vault, ...path.normalize("NFC").split("/")];
  return `/api/${api}/` + segments.map(encodeURIComponent).join("/");
}

/** The attachments Kumbu² keeps, by extension (the server checks the content against it). */
const ATTACHMENTS: Record<string, "image" | "pdf"> = { pdf: "pdf", png: "image", jpg: "image", jpeg: "image", gif: "image", webp: "image" };

function extensionOf(path: string): string {
  const name = path.slice(path.lastIndexOf("/") + 1);
  const dot = name.lastIndexOf(".");
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
}

/** "image" or "pdf" for a file Kumbu² keeps as an attachment, null for anything else (a note included). */
export function attachmentKind(path: string): "image" | "pdf" | null {
  return ATTACHMENTS[extensionOf(path)] ?? null;
}

/** The empty file that keeps a folder alive in Kumbu², which holds files only. */
export const MARKER = ".vaultkeep";

/** A marker file: named exactly MARKER, with at least one folder above it. Says nothing about dot folders. */
export function isMarker(path: string): boolean {
  const segments = path.split("/");
  return segments.length > 1 && segments[segments.length - 1] === MARKER;
}

/**
 * `attachments`: the server keeps PDFs and images, so they sync as notes do. "attachment" is any other file outside
 * dot folders, which stays on the "not synced" list.
 */
export function eligibility(path: string, attachments = false): "sync" | "attachment" | "ignored" {
  const segments = path.split("/");
  // Tested before the dot-segment rule: the marker itself is a dot file, and only its folders must be free of dots.
  if (isMarker(path)) return attachments && !segments.slice(0, -1).some((s) => s.startsWith(".")) ? "sync" : "ignored";
  if (segments.some((s) => s.startsWith("."))) return "ignored";
  if (path.toLowerCase().endsWith(".md")) return "sync";
  return attachments && attachmentKind(path) !== null ? "sync" : "attachment";
}

const pad = (n: number) => String(n).padStart(2, "0");

/**
 * "projects/Plan.md" → "projects/Plan (conflict Laptop 2026-10-27 10-15).md", local time. An attachment keeps its
 * extension: "shot.png" → "shot (conflict Laptop 2026-10-27 10-15).png". No device (a shared vault, or not knowing):
 * "projects/Plan (conflict 2026-10-27 10-15).md".
 */
export function conflictCopyPath(path: string, device: string | null, at: Date): string {
  const ext = extensionOf(path);
  const isNote = ext === "md";
  const stem = ext === "" ? path : path.slice(0, path.length - ext.length - 1);
  const stamp = `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ${pad(at.getHours())}-${pad(at.getMinutes())}`;
  return `${stem} (conflict ${device === null ? "" : `${device} `}${stamp})${isNote ? ".md" : ext === "" ? "" : path.slice(stem.length)}`;
}

export function slug(text: string): string {
  return text
    .normalize("NFD")
    .replace(/\p{M}/gu, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

/**
 * SecretStorage ids must be lowercase alphanumerics and dashes. SecretStorage is shared by every vault of the app,
 * so a short hash of the exact Kumbu² vault name and the Obsidian vault's name keeps ids apart that the slug folds
 * together ("Büro" and "Buro", names without Latin letters, two Obsidian vaults paired with one Kumbu² vault).
 */
export function secretId(vault: string, device: string, localVault: string): string {
  const hash = versionOfSync(`${vault}\n${localVault}`).slice(0, 6);
  return ["kumbu", slug(vault), hash, slug(device)].filter((part) => part !== "").join("-");
}

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

export function defaultDeviceName(platform: string, random: () => number): string {
  const pick = () => CROCKFORD[Math.min(CROCKFORD.length - 1, Math.floor(random() * CROCKFORD.length))];
  return `${platform}-${pick()}${pick()}`;
}

/** At most 32 characters: letters, digits, spaces, "-" and "_" (the server cuts writer labels at " ("). */
export function isValidDeviceName(name: string): boolean {
  const trimmed = name.trim();
  return trimmed.length > 0 && trimmed.length <= 32 && /^[\p{L}\p{N} _-]+$/u.test(trimmed);
}

export function isAllowedServerUrl(url: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  if (parsed.protocol === "https:") return true;
  return parsed.protocol === "http:" && (parsed.hostname === "localhost" || parsed.hostname === "127.0.0.1");
}

const URL_RULE = "The server URL must start with https:// (http:// only for localhost).";

/** Null when a key may be sent to this server, else the sentence to show. */
export function serverProblem(url: string): string | null {
  return isAllowedServerUrl(url) ? null : URL_RULE;
}
