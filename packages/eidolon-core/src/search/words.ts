// SPDX-License-Identifier: AGPL-3.0-or-later
// Words search over notes held in memory (offline mode design §3a, ADR 0031): the sections holding every word of the
// query, or the whole query in a line, title hits first, then by how often the query's words occur. A plain scan: for
// the few thousand notes of a vault it answers within a keystroke's debounce, with no index to build or keep.
import { textOrder } from "../links/order";
import { foldCase } from "../links/text";
import { isHeadingOnly, sectionsOf, snippetOf, titleOf, wordsOf, type Section } from "./sections";

/** A result in the shape of the server's search results; lines 1-based and inclusive, as it gives them. */
export interface WordsHit { path: string; address: string; startLine: number; endLine: number; snippet: string; reason: "title" | "exact" | "all words" }

/** A note read for searching: its sections with their words counted, once per version of its text. */
export interface PreparedNote {
  path: string;
  title: string;
  sections: { section: Section; words: Map<string, number>; folded: string }[];
}

export function prepare(path: string, content: string): PreparedNote {
  return {
    path,
    title: foldCase(titleOf(path)),
    // Headings with nothing below them say little; the server's search leaves them out too.
    sections: sectionsOf(path, content).filter((s) => !isHeadingOnly(s)).map((section) => {
      const words = new Map<string, number>();
      // As the server ranks by words: the note's name and the heading path count as the section's words.
      for (const w of wordsOf(`${section.context}\n${section.text}`)) words.set(w, (words.get(w) ?? 0) + 1);
      return { section, words, folded: foldCase(section.text.normalize("NFKC")) };
    }),
  };
}

/** At most `max` hits; ties keep the notes' path order and their sections' line order. */
export function searchWords(notes: readonly PreparedNote[], query: string, max: number): WordsHit[] {
  const terms = [...new Set(wordsOf(query))];
  const whole = foldCase(query.normalize("NFKC"));
  const inTitle = foldCase(query);
  const found: { path: string; section: Section; reason: WordsHit["reason"]; title: boolean; matches: number }[] = [];
  for (const note of [...notes].sort((a, b) => textOrder(a.path, b.path))) {
    const title = note.title.includes(inTitle);
    for (const { section, words, folded } of note.sections) {
      const exact = whole.length > 0 && folded.includes(whole);
      const all = terms.length > 0 && terms.every((t) => words.has(t));
      if (!exact && !all) continue;
      const matches = terms.reduce((n, t) => n + (words.get(t) ?? 0), 0);
      found.push({ path: note.path, section, reason: title ? "title" : exact ? "exact" : "all words", title, matches });
    }
  }
  // Array.prototype.sort is stable: equal ones stay in path and line order.
  found.sort((a, b) => Number(b.title) - Number(a.title) || b.matches - a.matches);
  return found.slice(0, max).map(({ path, section, reason }) => ({
    path, address: section.address, startLine: section.startLine + 1, endLine: section.endLine, snippet: snippetOf(section, query), reason,
  }));
}
