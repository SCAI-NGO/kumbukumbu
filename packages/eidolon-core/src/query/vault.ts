// SPDX-License-Identifier: AGPL-3.0-or-later
// The files one run of a query sees (Kumbu.Core Queries/QueryVault), for what reaches beyond the row's own file: file()
// and link(), a file's links and backlinks, and the random() of each row, drawn once per run (bases spec 2026-11-25
// decision 16). Links resolve as everywhere else in Kumbu² (LinkResolver), worked out when a query first asks.
import { aliasesOf } from "../links/aliases";
import { parseLinks } from "../links/parse";
import { LinkResolver, type LinkResolution } from "../links/resolve";
import type { QueryNote } from "./note";
import type { Value } from "./value";

// The resolver works on paths with a vault; the files of one run are of one vault, whatever its name.
const VAULT = "vault";
const full = (path: string) => `/${VAULT}/${path}`;

/** A link of a note as a query sees it: the path in the vault of the file it leads to, or its target as written when it
 * leads to none, and whether it embeds. */
export type QueryLink = { readonly path: string; readonly embed: boolean };

export class QueryVault {
  private readonly byPath = new Map<string, QueryNote>();
  private readonly linkCache = new Map<QueryNote, readonly QueryLink[]>();
  private readonly randoms = new Map<string, number>();
  private readonly kept = new Map<string, Value>();
  private resolverCache: LinkResolver | null = null;
  private backlinkCache: Map<string, QueryNote[]> | null = null;

  constructor(files: Iterable<QueryNote>) {
    for (const file of files) this.byPath.set(file.path, file);
  }

  /** The file at a path in the vault, if there is one. */
  file(path: string): QueryNote | null {
    return this.byPath.get(path) ?? null;
  }

  /** Where a wikilink to `target` in `from` leads: a file's path in the vault, or null when it leads to none (or to several). */
  resolve(from: QueryNote, target: string): string | null {
    return this.inVault(this.resolver.resolve(full(from.path), target));
  }

  /** The links of a note's text, embeds too, in the order written; a link to a heading of the note itself isn't one.
   * None for a file that isn't a note. */
  linksOf(note: QueryNote): readonly QueryLink[] {
    const known = this.linkCache.get(note);
    if (known !== undefined) return known;
    const found: QueryLink[] = [];
    if (note.isNote) {
      for (const link of parseLinks(note.content)) {
        if (link.target.trim().length === 0) continue;
        found.push({ path: this.inVault(this.resolver.resolve(full(note.path), link)) ?? link.target.trim(), embed: link.embed });
      }
    }
    this.linkCache.set(note, found);
    return found;
  }

  /** The files whose links lead to `note`, each once, in path order; worked out for the whole vault the first time it is asked. */
  backlinksOf(note: QueryNote): readonly QueryNote[] {
    if (this.backlinkCache === null) {
      const backlinks = new Map<string, QueryNote[]>();
      const sources = [...this.byPath.values()].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
      for (const source of sources) {
        for (const target of new Set(this.linksOf(source).map((l) => l.path))) {
          if (!this.byPath.has(target)) continue;
          const list = backlinks.get(target);
          if (list === undefined) backlinks.set(target, [source]);
          else list.push(source);
        }
      }
      this.backlinkCache = backlinks;
    }
    return this.backlinkCache.get(note.path) ?? [];
  }

  /** A file's field as a list or an object (its tags, links, backlinks, properties), built the first time the run asks for
   * it; the same value after, so asking again costs nothing and its measure is kept. */
  keptOf(note: QueryNote, field: string): Value | undefined {
    return this.kept.get(`${field}\u0000${note.path}`);
  }

  keep(note: QueryNote, field: string, value: Value): Value {
    this.kept.set(`${field}\u0000${note.path}`, value);
    return value;
  }

  /** The row's random number in [0, 1): the same for every use in the run. */
  random(note: QueryNote): number {
    let value = this.randoms.get(note.path);
    if (value === undefined) {
      value = Math.random();
      this.randoms.set(note.path, value);
    }
    return value;
  }

  private get resolver(): LinkResolver {
    if (this.resolverCache === null) {
      const files = [...this.byPath.values()];
      const notes = files.filter((f) => f.isNote);
      this.resolverCache = new LinkResolver(
        VAULT,
        notes.map((n) => full(n.path)),
        files.filter((f) => !f.isNote).map((f) => full(f.path)),
        new Map(notes.map((n) => [full(n.path), aliasesOf(n.content)])),
      );
    }
    return this.resolverCache;
  }

  private inVault(resolution: LinkResolution): string | null {
    if ((resolution.kind !== "note" && resolution.kind !== "attachment") || resolution.target === null) return null;
    const path = resolution.target.slice(VAULT.length + 2);
    return this.byPath.has(path) ? path : null;
  }
}
