// SPDX-License-Identifier: AGPL-3.0-or-later
import type { QueryClient } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { findFolder, levelOf, mayChange, moveNote, type TreeFolder, type VaultView } from "../api/notes";
import { folderOf, inVault, vaultOf } from "../api/paths";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { presenceKey, useSyncStatus } from "../state/sync";
import { headingText } from "./outline";
import { splitFrontmatter } from "./properties";

/** A name that carries no intention: `Untitled.md` as the app and Obsidian make them, `Untitled 2.md`, `Untitled 1.md` …, any case. */
export const isUntitled = (path: string) => /^untitled(?: [1-9]\d*)?\.md$/i.test(path.slice(path.lastIndexOf("/") + 1));

const MAX_NAME = 100;

/**
 * The heading's text as a file name: NFC, without the characters Obsidian and common file systems refuse, one space
 * for any run of whitespace, no dots or spaces at either end (WebDAV and Windows clients choke on trailing ones), at
 * most 100 characters, cut at a word where there is one. Null when nothing is left. Convenience only: the server checks.
 */
export function cleanName(text: string): string | null {
  const name = text
    .normalize("NFC")
    .replace(/[/\\:*?"<>|#^[\]]/g, "")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/^[. ]+/, "");
  let chars = [...name];
  if (chars.length > MAX_NAME) {
    const space = chars.slice(0, MAX_NAME + 1).lastIndexOf(" ");
    chars = chars.slice(0, space > 0 ? space : MAX_NAME);
  }
  return chars.join("").replace(/[. ]+$/, "") || null;
}

/**
 * The name the note's first level-1 heading gives it, without `.md`: ATX only (`# Einkaufsliste`), in the body
 * below the front matter, never inside fenced code or a `%% … %%` comment (an unclosed `%%` hides the rest). Null
 * when that heading is empty or leaves no name; a later heading doesn't count then.
 */
export function headingName(content: string): string | null {
  let fence: string | null = null;
  let comment = false;
  for (const raw of splitFrontmatter(content).body.split("\n")) {
    const source = raw.replace(/\r$/, "");
    if (!comment) {
      const marker = /^ {0,3}(`{3,}|~{3,})/.exec(source)?.[1];
      if (marker && (fence === null || (marker[0] === fence[0] && marker.length >= fence.length))) {
        fence = fence === null ? marker : null;
        continue;
      }
      if (fence !== null) continue;
    }
    let line = "";
    for (let rest = source; ; ) {
      const at = rest.indexOf("%%");
      if (at < 0) {
        if (!comment) line += rest;
        break;
      }
      if (!comment) line += rest.slice(0, at);
      comment = !comment;
      rest = rest.slice(at + 2);
    }
    const m = /^ {0,3}#(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/.exec(line);
    // Highlight and maths marks are no part of the name.
    if (m) return m[1] ? cleanName(headingText(m[1]).replace(/==|\$/g, "")) : null;
  }
  return null;
}

/**
 * Where an untitled note goes once its heading names it (`/Home/Garden/Einkaufsliste.md`, else `… 2.md`, `… 3.md`:
 * the first name no note or folder beside it has, ignoring case). Null for a named note, a note without such a
 * heading, a heading that is itself an untitled name, and a card in a board's folder, which the board names.
 */
export function renameTarget(path: string, content: string, root: TreeFolder): string | null {
  if (!isUntitled(path)) return null;
  const name = headingName(content);
  if (!name || isUntitled(`${name}.md`)) return null;
  const folder = folderOf(path);
  const at = findFolder(root, folder);
  if (folder === `/${vaultOf(path)}/Board` && at?.notes.some((n) => n.name === "Board.md")) return null;
  const taken = new Set([...(at?.notes.filter((n) => n.path !== path) ?? []), ...(at?.folders ?? [])].map((e) => e.name.toLowerCase()));
  let file = `${name}.md`;
  for (let i = 2; taken.has(file.toLowerCase()); i++) file = `${name} ${i}.md`;
  return `${folder}/${file}`;
}

/** How long the tree and the move may each take: Write mode for the note waits on them. */
const RENAME_TIMEOUT = 15_000;

class TimedOut extends Error {}

/** `run` with a signal that aborts after the timeout; a timeout rejects with `TimedOut`, whatever the request does then. */
async function timed<T>(run: (signal: AbortSignal) => Promise<T>): Promise<T> {
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => {
      controller.abort();
      reject(new TimedOut());
    }, RENAME_TIMEOUT);
  });
  try {
    return await Promise.race([run(controller.signal), timeout]);
  } finally {
    clearTimeout(timer);
  }
}

/** Renames go one after another, each looking at the folder afresh: two notes may want the same name. */
let queue: Promise<void> = Promise.resolve();
/** Counted up by `resetRenames`: what was queued before doesn't run. */
let generation = 0;
/** The renames waiting for the editor's unmount to settle, by path. */
const pending = new Map<string, ReturnType<typeof setTimeout>>();

/**
 * Write mode on `path` ended with `saved` saved: the rename runs once the unmount has settled, unless an editor for
 * the note mounts again first (StrictMode does so at once), which `cancelRename` stands for.
 */
export function scheduleRename(queryClient: QueryClient, path: string, saved: string) {
  if (!isUntitled(path) || headingName(saved) === null) return;
  cancelRename(path);
  pending.set(
    path,
    setTimeout(() => {
      pending.delete(path);
      void renameFromHeading(queryClient, path, saved);
    }, 0),
  );
}

export function cancelRename(path: string) {
  clearTimeout(pending.get(path));
  pending.delete(path);
}

/** Forgets every rename waiting or queued (tests: none outlives its test). */
export function resetRenames() {
  for (const timer of pending.values()) clearTimeout(timer);
  pending.clear();
  queue = Promise.resolve();
  generation++;
}

/**
 * Names an untitled note from the heading of `saved`, the text its editor last saved, once Write mode has ended with
 * everything saved (spec §2): through the move, which rewrites the links to it. Nothing is sent without such a
 * heading; nor without write permission or a session, while anyone else edits the note (renamed under them, it would
 * be gone for them), or once the note is no longer where it was (moved by hand meanwhile). Queued behind any rename
 * under way; Write mode waits for it. The open note, the recent notes and the URL follow it at once, also when the
 * editor's page is gone by then. A failure is said and left as it is: not tried again by itself.
 */
export function renameFromHeading(queryClient: QueryClient, path: string, saved: string): Promise<void> {
  if (!isUntitled(path) || headingName(saved) === null || useEditor.getState().renaming.includes(path)) return Promise.resolve();
  useEditor.getState().setRenaming(path, true);
  const at = generation;
  const run = queue.then(() => (at === generation ? renameNow(queryClient, path, saved) : undefined)).finally(() => useEditor.getState().setRenaming(path, false));
  queue = run.catch(() => {});
  return run;
}

async function renameNow(queryClient: QueryClient, path: string, saved: string) {
  if (!useSession.getState().user || useEditor.getState().refused?.path === path) return;
  const vault = vaultOf(path);
  if (!mayChange(levelOf(queryClient.getQueryData<VaultView[]>(["vaults"])?.find((v) => v.name === vault)), path)) return;
  if (useSyncStatus.getState().editors[presenceKey(vault, inVault(path))]?.length) return;
  try {
    // Asked afresh: the names taken beside it are what decides.
    const tree = await timed((signal) =>
      queryClient.fetchQuery({
        queryKey: ["tree", vault],
        queryFn: () => call(api.GET("/api/vaults/{vault}/tree", { params: { path: { vault } }, signal })),
        staleTime: 0,
      }),
    );
    if (!findFolder(tree.root, folderOf(path))?.notes.some((n) => n.path === path)) return;
    const to = renameTarget(path, saved, tree.root);
    // Signed out meanwhile: the move would only be refused.
    if (!to || !useSession.getState().user) return;
    // Exact: a folder of that name made meanwhile would otherwise take the note in.
    await timed((signal) => moveNote(queryClient, path, to, { exact: true, signal }));
    useEditor.getState().moved(path, to);
    useEditor.getState().follow?.(path, to);
  } catch (e) {
    const why = e instanceof TimedOut ? "The server didn't answer in time." : errorText(e);
    useEditor.getState().setRenameProblem({ path, text: `Couldn't name the note from its heading: ${why}` });
  }
}
