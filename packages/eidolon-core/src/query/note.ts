// SPDX-License-Identifier: AGPL-3.0-or-later
// A file as a query reads it (Kumbu.Core Queries/QueryNote): its path in the vault, its text, its typed properties, its
// size in bytes and its times (null where they aren't known). A note is a .md file; any other file (a .base file, an
// attachment) is a row too, with no properties and no tags.
import { foldCase, frontmatterEnd, splitLines } from "../links/text";
import type { NoteProperties } from "./properties";
import { tagsOf } from "./tags";

const encoder = new TextEncoder();

export class QueryNote {
  private tagList: readonly string[] | undefined;
  /** The size in bytes; left out, the UTF-8 length of the text, as a note's is. */
  readonly size: number;

  constructor(
    readonly path: string,
    readonly content: string,
    readonly properties: NoteProperties,
    size: number | null = null,
    /** "yyyy-MM-ddTHH:mm:ss" without a zone, as now() is. */
    readonly created: string | null = null,
    readonly modified: string | null = null,
  ) {
    this.size = size ?? encoder.encode(content).byteLength;
  }

  static isNotePath(path: string): boolean {
    return path.endsWith(".md");
  }

  get isNote(): boolean {
    return QueryNote.isNotePath(this.path);
  }

  /**
   * Its tags as Obsidian's file.tags gives them: with their '#', the text's before the frontmatter's (finding 6), each
   * once (ignoring case), as first written; none for a file that isn't a note.
   */
  get tags(): readonly string[] {
    if (this.tagList === undefined) {
      const first = new Map<string, string>();
      if (this.isNote) {
        const end = frontmatterEnd(splitLines(this.content));
        const uses = tagsOf(this.content);
        for (const { tag } of [...uses.filter((u) => u.line >= end), ...uses.filter((u) => u.line < end)])
          if (!first.has(foldCase(tag))) first.set(foldCase(tag), `#${tag}`);
      }
      this.tagList = [...first.values()];
    }
    return this.tagList;
  }
}
