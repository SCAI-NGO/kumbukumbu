// SPDX-License-Identifier: AGPL-3.0-or-later
// Words search across the held vaults, as `GET /api/search` answers it without meaning (offline mode design §3a):
// the local index, built in memory from the copy's texts and never stored (§3, ADR 0031).
import type { SealedMirror } from "../store/mirror";
import { DamagedRecordError } from "../store/vault";
import { inArchive } from "../links/archive";
import { isFolderMarker, kindOf } from "../links/formats";
import { carries, normaliseTag, tagsOf } from "../search/tags";
import { prepare, searchWords, type PreparedNote, type WordsHit } from "../search/words";

/** No note in the held vaults carries the tag: the server's 404, so a misspelt tag says so instead of finding nothing. */
export class NoSuchTagError extends Error {
  constructor(readonly tag: string) { super(`No note carries #${tag}.`); }
}

interface Held { version: string; note: PreparedNote; tags: string[] }

/** Opens each note's text once per version: a search after the first reads only what changed since. */
export class MirrorSearch {
  private held = new Map<string, Held>();

  constructor(private readonly mirror: SealedMirror) {}

  /**
   * The best `max` sections for `query` in every held vault's notes. With `tag`, only the notes that carry it or a
   * tag nested below it; with `archive`, the archive's notes too, which are left out otherwise.
   */
  async find(query: string, { max, tag = null, archive = false }: { max: number; tag?: string | null; archive?: boolean }): Promise<WordsHit[]> {
    const seen = new Map<string, Held>();
    for (const vault of this.mirror.held()) {
      if (vault.name === undefined) continue;
      for (const e of vault.list()) {
        const path = `/${vault.name}/${e.path}`;
        if (e.local === undefined || vault.removedHere(e.path) || e.path.split("/").some((s) => s.startsWith("."))) continue;
        if (isFolderMarker(path) || kindOf(path) !== "note") continue;
        const key = `${vault.id}\0${path}`;
        let held = this.held.get(key);
        if (held?.version !== e.local.version) {
          // A note damaged on this device is left out rather than failing the whole search.
          const bytes = await vault.bytes(e.path).catch((err: unknown) => {
            if (err instanceof DamagedRecordError) return null;
            throw err;
          });
          if (bytes === null) continue;
          const content = new TextDecoder().decode(bytes);
          held = { version: e.local.version, note: prepare(path, content), tags: tagsOf(content) };
        }
        seen.set(key, held);
      }
    }
    // What went from the copy goes from memory too.
    this.held = seen;

    let notes = [...seen.values()];
    if (tag !== null) {
      const asked = normaliseTag(tag) ?? tag;
      const tagged = notes.filter((h) => carries(h.tags, asked));
      if (tagged.length === 0) throw new NoSuchTagError(asked);
      notes = tagged;
    }
    if (!archive) notes = notes.filter((h) => !inArchive(h.note.path));
    return searchWords(notes.map((h) => h.note), query, max);
  }
}
