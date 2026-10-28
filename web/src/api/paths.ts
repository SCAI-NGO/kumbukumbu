// SPDX-License-Identifier: AGPL-3.0-or-later

// Paths as the API gives them: from the root of the vault tree, the vault first ("/Work/projects/plan.md").

/** The app's URL of a note, each segment encoded: vault and note names are content, not URL syntax. */
export const noteUrl = (path: string) => `/notes/${path.split("/").slice(1).map(encodeURIComponent).join("/")}`;

/** The note path of `/notes/:vault/*`, from React Router's (decoded) params. */
export const pathFromRoute = (vault: string, rest: string) => `/${vault}/${rest}`;

/** The app's URL of a checkpoint, and of a note in it (`path` from the root of the tree, the vault first). */
export const checkpointUrl = (vault: string, name: string, path?: string) =>
  `/checkpoints/${encodeURIComponent(vault)}/${encodeURIComponent(name)}` +
  (path ? `/${inVault(path).split("/").map(encodeURIComponent).join("/")}` : "");

/** The app's URL of a vault's board, or of a checkpoint's board as it was. */
export const boardUrl = (vault: string, checkpoint?: string) =>
  `/board/${encodeURIComponent(vault)}` + (checkpoint ? `?checkpoint=${encodeURIComponent(checkpoint)}` : "");

/** The app's URL of a vault's gallery: its images and PDFs. */
export const galleryUrl = (vault: string) => `/gallery/${encodeURIComponent(vault)}`;

/** What a path holds by its extension, as the server tells files apart (`kind`): "image", "pdf" or "note". */
export type FileKind = "note" | "image" | "pdf";

/** The formats Kumbu² keeps besides notes, as the server accepts them (any case). */
const attachmentKinds: Record<string, FileKind> = { ".pdf": "pdf", ".png": "image", ".jpg": "image", ".jpeg": "image", ".gif": "image", ".webp": "image" };

export function fileKindOf(path: string): FileKind {
  const dot = path.lastIndexOf(".");
  return (dot > path.lastIndexOf("/") && attachmentKinds[path.slice(dot).toLowerCase()]) || "note";
}

export const isAttachment = (path: string) => fileKindOf(path) !== "note";

/** The API's URL of an attachment's bytes (`/api/files/{vault}/{path}`), at `version` if given. */
export const fileUrl = (vault: string, path: string, version?: string) =>
  `/api/files/${encodeURIComponent(vault)}/${path.split("/").map(encodeURIComponent).join("/")}` +
  (version ? `?version=${encodeURIComponent(version)}` : "");

/** Which state of an attachment: an earlier `version` from its history (or the trash), or as `checkpoint` held it; else today's. */
export type FileAt = { version?: string; checkpoint?: string };

/** The API's URL of an attachment's bytes at `at`: today's or a version (`fileUrl`), or a checkpoint's. */
export const fileAtUrl = (vault: string, path: string, at: FileAt = {}) =>
  at.checkpoint
    ? `/api/vaults/${encodeURIComponent(vault)}/checkpoints/${encodeURIComponent(at.checkpoint)}/files/${path.split("/").map(encodeURIComponent).join("/")}`
    : fileUrl(vault, path, at.version);

/**
 * The app's URL of an attachment in the lightbox (`/view/:vault/*`), read only at an earlier version or a checkpoint
 * (`?version=`, `?checkpoint=`). It is opened with the paths to page through as router state (`LightboxState`): a
 * folder's attachments from the tree, a note's from its chips.
 */
export const viewUrl = (path: string, at: FileAt = {}) =>
  `/view/${path.split("/").slice(1).map(encodeURIComponent).join("/")}` +
  (at.checkpoint ? `?checkpoint=${encodeURIComponent(at.checkpoint)}` : at.version ? `?version=${encodeURIComponent(at.version)}` : "");

/** The lightbox's router state: the attachments (paths from the root of the tree) in the order it pages through. */
export type LightboxState = { paths: string[] };

export const vaultOf = (path: string) => path.split("/")[1];

export const folderOf = (path: string) => path.slice(0, path.lastIndexOf("/"));

/** The path within its vault, as `{path}` of `/api/notes/{vault}/{path}` wants it. */
export const inVault = (path: string) => path.split("/").slice(2).join("/");

/** A note's file name without `.md`; an attachment's keeps its extension (`shot.png`), which tells it apart. */
export const titleOf = (path: string) => path.slice(path.lastIndexOf("/") + 1).replace(/\.md$/, "");

export const crumbOf = (path: string) => folderOf(path).split("/").slice(1).join(" / ");

/** `Untitled.md`, else `Untitled 2.md`, `Untitled 3.md` …: the first name no note in the folder has, ignoring case. */
export function untitledName(taken: string[]) {
  const names = new Set(taken.map((n) => n.toLowerCase()));
  let name = "Untitled.md";
  for (let i = 2; names.has(name.toLowerCase()); i++) name = `Untitled ${i}.md`;
  return name;
}
