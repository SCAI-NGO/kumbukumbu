// SPDX-License-Identifier: AGPL-3.0-or-later
// Reading from the offline copy (offline mode design §3, work package 5). While the app is offline with an unlocked
// copy, the mode (work package 8) sets the source, and the tree, notes, links, embeds and attachments are answered
// from the copy in the shapes the API gives them, so the screens that work offline need no second path of their own.
import type { SealedMirror } from "eidolon-core/store/mirror";
import { MirrorLinks } from "eidolon-core/read/links";
import { MirrorSearch, NoSuchTagError } from "eidolon-core/read/search";
import { locate, NotOnDeviceError, readFile, readNote, treeOf, vaultByName } from "eidolon-core/read/tree";
import { HttpError } from "../api/client";
import type { NoteView, TreeView } from "../api/notes";
import type { LinkView } from "../note/wikiLinks";
import type { SearchView } from "../search/useSearch";

/** The search sheet's notice line offline (design §3a), where the server says when meaning isn't available. */
export const OFFLINE_SEARCH = "You're offline: search finds words only. Search by meaning is back when you are.";

/** Listed in the copy, but its text or bytes haven't come yet: no 404 (it exists), no retry (it won't come offline). */
export const NOT_ON_DEVICE = "not-on-device";

export const isNotOnDevice = (err: unknown): boolean => err instanceof HttpError && err.type === NOT_ON_DEVICE;

function rethrown(err: unknown): never {
  if (err instanceof NotOnDeviceError) throw new HttpError(0, err.message, NOT_ON_DEVICE);
  throw err;
}

export class OfflineSource {
  private readonly links: MirrorLinks;
  private readonly words: MirrorSearch;

  constructor(readonly mirror: SealedMirror) {
    this.links = new MirrorLinks(mirror);
    this.words = new MirrorSearch(mirror);
  }

  /** The vault of a path (`/Work/plan.md`) and the path within it, for the copy's writers too (work package 7). */
  vaultFor(path: string) {
    const found = locate(this.mirror, path);
    return found === undefined ? null : { mirror: found.vault, inVault: found.inVault };
  }

  tree(vault: string): TreeView {
    const held = vaultByName(this.mirror, vault);
    // As the server's 404 for a vault the caller hasn't, in the words of design §3a for one not kept here.
    if (held === undefined) throw new HttpError(404, `${vault} is not kept on this device.`);
    return treeOf(held, vault) as TreeView;
  }

  async note(path: string): Promise<NoteView> {
    const note = await readNote(this.mirror, path).catch(rethrown);
    if (note === null) throw new HttpError(404, `There is no note at ${path} on this device.`);
    return note;
  }

  linksOf(path: string, content: string): Promise<LinkView[]> {
    return this.links.of(path, content);
  }

  /** `GET /api/search` by words, whatever the mode: meaning is the server's (§3a). Words only, so no score. */
  async search(q: string, max: number, tag: string | null, archive: boolean): Promise<SearchView> {
    try {
      const hits = await this.words.find(q, { max, tag, archive });
      return { results: hits.map((h) => ({ ...h, score: null })), notIndexed: 0, notice: OFFLINE_SEARCH };
    } catch (err) {
      // As the server's 404, so a misspelt tag says so instead of finding nothing.
      if (err instanceof NoSuchTagError) throw new HttpError(404, err.message);
      throw err;
    }
  }

  /** Whether the copy holds the path's text or bytes; null for a path it doesn't list. */
  held(path: string): boolean | null {
    const found = locate(this.mirror, path);
    const entry = found?.vault.entry(found.inVault);
    return entry === undefined ? null : entry.local !== undefined;
  }

  /** An attachment's bytes as a Blob of its media type. */
  async file(path: string, mediaType: string): Promise<Blob> {
    const bytes = await readFile(this.mirror, path).catch(rethrown);
    if (bytes === null) throw new HttpError(404, `There is no file at ${path} on this device.`);
    return new Blob([bytes], { type: mediaType });
  }
}

let current: OfflineSource | null = null;

/** Offline, an attachment the copy lists without its bytes: its place says "Opens when you're online" (design §3a). */
export const notOnDevice = (path: string): boolean => current?.held(path) === false;

/** What stands where an attachment not on this device would be shown. */
export const OPENS_ONLINE = "Opens when you're online.";

/** The copy the app reads from now; null online, or offline without an unlocked copy. */
export const offlineSource = (): OfflineSource | null => current;

/**
 * Set by the mode on entering offline mode with an unlocked copy, cleared on leaving it or locking. The caller asks
 * the queries `tree`, `note`, `links` and `version` again on every switch, so what is shown comes from the right side.
 */
export function setOfflineSource(source: OfflineSource | null): void {
  current = source;
}

/**
 * Keeps each held vault's name as the vault list gives it now (renamed, or a member's alias changed): offline the
 * paths name vaults by it, and a contact carries no names. Called after every contact online.
 */
export async function nameVaults(mirror: SealedMirror, vaults: readonly { id?: string | null; name: string }[]): Promise<void> {
  for (const held of mirror.held()) {
    const listed = vaults.find((v) => v.id === held.id);
    if (listed !== undefined && listed.name !== held.name) await held.setMeta({ name: listed.name });
  }
}
