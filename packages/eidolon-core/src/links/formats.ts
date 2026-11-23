// SPDX-License-Identifier: AGPL-3.0-or-later
// The file formats Kumbu² keeps besides notes, by name (Kumbu.Core Attachments/AttachmentFormat, FolderMarker and
// Skills/SkillPaths): the parts a client needs to tell files apart without their bytes.

export type FileKind = "note" | "image" | "pdf" | "folder" | "text" | "file";

interface Format { extension: string; mediaType: string; kind: Exclude<FileKind, "note"> }

/** The empty file that keeps an otherwise empty folder, directly inside it. */
export const FOLDER_MARKER = ".vaultkeep";

const FORMATS: readonly Format[] = [
  { extension: ".pdf", mediaType: "application/pdf", kind: "pdf" },
  { extension: ".png", mediaType: "image/png", kind: "image" },
  { extension: ".jpg", mediaType: "image/jpeg", kind: "image" },
  { extension: ".jpeg", mediaType: "image/jpeg", kind: "image" },
  { extension: ".gif", mediaType: "image/gif", kind: "image" },
  { extension: ".webp", mediaType: "image/webp", kind: "image" },
  { extension: ".svg", mediaType: "image/svg+xml", kind: "image" },
];

const OCTET_STREAM = "application/octet-stream";
export const MARKER: Format = { extension: FOLDER_MARKER, mediaType: OCTET_STREAM, kind: "folder" };
const TEXT: Format = { extension: "", mediaType: "text/plain; charset=utf-8", kind: "text" };
/** Any file of no other format; the server decides its media type by its content at each write. */
export const FILE: Format = { extension: "", mediaType: OCTET_STREAM, kind: "file" };

const TEXT_EXTENSIONS = [".py", ".sh", ".js", ".mjs", ".ts", ".json", ".yaml", ".yml", ".toml", ".txt", ".csv", ".xml", ".html", ".css", ".svg"];
const TEXT_NAMES = new Set(["LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS", "CHANGELOG", "README", "MAKEFILE", "DOCKERFILE"]);

/** The vault marked as the skills vault, as the server names it by default. */
export const SKILLS_VAULT = "Skills";

/** The format a file name's extension names (case-insensitive); null for a note's name, FILE for any other. */
export function formatForName(name: string): Format | null {
  if (name.endsWith(".md")) return null;
  // Exactly the marker's name; a whole path names it as its last segment.
  if (name === FOLDER_MARKER || name.endsWith("/" + FOLDER_MARKER)) return MARKER;
  const lower = name.toLowerCase();
  return FORMATS.find((f) => lower.endsWith(f.extension)) ?? FILE;
}

function segmentsOf(path: string): string[] {
  return path.split("/").filter((s) => s.length > 0);
}

/** Strictly below a skill's folder: /<skills vault>/<skill>/… or /<vault>/_skills/<skill>/… */
function inSkillFolder(segments: readonly string[], skillsVault: string | null): boolean {
  const depth = skillsVault !== null && segments[0] === skillsVault ? 1 : segments[1] === "_skills" ? 2 : 0;
  return depth > 0 && segments.length > depth + 1;
}

function formatForPath(path: string, skillsVault: string | null): Format | null {
  const segments = segmentsOf(path);
  const name = segments[segments.length - 1];
  if (name === undefined) return null;
  const lower = name.toLowerCase();
  const textName = TEXT_NAMES.has(name.toUpperCase()) || TEXT_EXTENSIONS.some((e) => lower.endsWith(e));
  if (textName && inSkillFolder(segments, skillsVault)) return TEXT;
  return formatForName(name);
}

/** What a path holds, as clients tell files apart; "text" only for a text file inside a skill's folder.
 * `skillsVault` is the caller's name for the skills vault, null when it has none. */
export function kindOf(path: string, skillsVault: string | null = SKILLS_VAULT): FileKind {
  const format = path.startsWith("/") && segmentsOf(path).length > 0 ? formatForPath(path, skillsVault) : formatForName(path);
  return format?.kind ?? "note";
}

/** The media type the format table gives a path: text/markdown for a note; application/octet-stream for a file of no
 * known format, whose real type only its content tells. */
export function mediaTypeOf(path: string, skillsVault: string | null = SKILLS_VAULT): string {
  return formatForPath(path, skillsVault)?.mediaType ?? "text/markdown";
}

export function isFolderMarker(path: string): boolean {
  return path.slice(path.lastIndexOf("/") + 1) === FOLDER_MARKER && segmentsOf(path).length > 0;
}
