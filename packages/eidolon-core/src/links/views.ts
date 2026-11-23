// SPDX-License-Identifier: AGPL-3.0-or-later
// The links of a note as GET /api/links answers them (Kumbu.Server Api/NoteApi LinkView, LinkViewsAsync).
import { textOrder } from "./order";
import { parseLinks } from "./parse";
import type { LinkKind, LinkResolver } from "./resolve";

/** `line` is 1-based; `path` where the link leads; `candidates` only for an ambiguous link. */
export interface LinkView { line: number; raw: string; target: string; kind: LinkKind; path: string | null; candidates: string[] }

/** The links in `content`, the note at `path` or a version of it, resolved by `resolver`. */
export function linkViews(resolver: LinkResolver, path: string, content: string): LinkView[] {
  return parseLinks(content).map((link) => {
    const r = resolver.resolve(path, link);
    return {
      line: link.line + 1,
      raw: link.raw,
      target: link.target,
      kind: r.kind,
      path: r.target,
      candidates: r.kind === "ambiguous" ? [...r.candidates].sort(textOrder) : [],
    };
  });
}
