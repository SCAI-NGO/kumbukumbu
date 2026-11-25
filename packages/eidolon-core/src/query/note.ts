// SPDX-License-Identifier: AGPL-3.0-or-later
// A note as a query reads it (Kumbu.Core Queries/QueryNote): its path in the vault, its text and its typed properties.
import { foldCase } from "../links/text";
import type { NoteProperties } from "./properties";
import { tagsOf } from "./tags";

export class QueryNote {
  private tagList: readonly string[] | undefined;

  constructor(
    readonly path: string,
    readonly content: string,
    readonly properties: NoteProperties,
  ) {}

  /** Its tags as Obsidian reads them, frontmatter first, each once (ignoring case), as first written. */
  get tags(): readonly string[] {
    if (this.tagList === undefined) {
      const first = new Map<string, string>();
      for (const { tag } of tagsOf(this.content)) if (!first.has(foldCase(tag))) first.set(foldCase(tag), tag);
      this.tagList = [...first.values()];
    }
    return this.tagList;
  }
}
