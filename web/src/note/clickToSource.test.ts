// SPDX-License-Identifier: AGPL-3.0-or-later
import { createElement } from "react";
import { cleanup, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, describe, expect, it, vi } from "vitest";
import { offsetAt, sourceOffset } from "./clickToSource";
import MarkdownView from "./MarkdownView";

afterEach(cleanup);

function show(source: string) {
  const { container } = render(
    createElement(
      QueryClientProvider,
      { client: new QueryClient() },
      createElement(MemoryRouter, null, createElement(MarkdownView, { source, links: [], tasksDisabled: false, onToggleTask: vi.fn(), path: "/Work/Note.md" })),
    ),
  );
  return container;
}

/** The text node holding the `nth` (0-based) `word` of the rendered note, and the point `into` characters into it. */
function pointIn(root: HTMLElement, word: string, nth = 0, into = 1): { block: HTMLElement; node: Text; offset: number } {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  let seen = 0;
  for (let node = walker.nextNode() as Text | null; node; node = walker.nextNode() as Text | null) {
    let at = node.data.indexOf(word);
    while (at >= 0) {
      if (seen++ === nth) return { block: node.parentElement!.closest<HTMLElement>("[data-line]")!, node, offset: at + into };
      at = node.data.indexOf(word, at + 1);
    }
  }
  throw new Error(`no ${word} #${nth}`);
}

/** Where the `nth` `word` of `source` starts, plus `into`. */
const sourceAt = (source: string, word: string, nth = 0, into = 1) => {
  let at = -1;
  for (let i = 0; i <= nth; i++) at = source.indexOf(word, at + 1);
  return at + into;
};

/** Clicks into the `nth` `word` as rendered and gives the source offset the click maps to. */
function clicked(source: string, word: string, nth = 0, into = 1) {
  const { block, node, offset } = pointIn(show(source), word, nth, into);
  return offsetAt(source, block, node, offset);
}

describe("a click mapped to the source", () => {
  it("plain text: the caret where the word is", () => {
    const source = "# Title\n\nThe pump needs a new controller today.\n";
    expect(clicked(source, "controller", 0, 3)).toBe(sourceAt(source, "controller", 0, 3));
  });

  it("a paragraph over two lines: the second line", () => {
    const source = "# Title\n\nThe pump needs\na new controller today.\n";
    expect(clicked(source, "controller")).toBe(sourceAt(source, "controller"));
  });

  it("emphasis: inside the bold word, and in the words after it", () => {
    const source = "Some **bold** words and _more_ here.\n";
    expect(clicked(source, "bold", 0, 2)).toBe(sourceAt(source, "bold", 0, 2));
    expect(clicked(source, "words")).toBe(sourceAt(source, "words"));
    expect(clicked(source, "here")).toBe(sourceAt(source, "here"));
  });

  it("a link's label, a wiki link's and a Markdown link's", () => {
    const source = "See [[Plan|the plan]] and [the docs](https://example.org/docs) for more.\n";
    expect(clicked(source, "plan", 0, 2)).toBe(sourceAt(source, "plan", 0, 2));
    expect(clicked(source, "docs", 0, 2)).toBe(sourceAt(source, "docs", 0, 2));
    expect(clicked(source, "more")).toBe(sourceAt(source, "more"));
  });

  it("list items: the item clicked", () => {
    const source = "- first item\n- second item\n- third item\n";
    expect(clicked(source, "item", 1)).toBe(sourceAt(source, "item", 1));
    expect(clicked(source, "third")).toBe(sourceAt(source, "third"));
  });

  it("a quote", () => {
    const source = "> quoted words\n> over two lines\n";
    expect(clicked(source, "words")).toBe(sourceAt(source, "words"));
    expect(clicked(source, "lines")).toBe(sourceAt(source, "lines"));
  });

  it("code, inline and in a block", () => {
    const source = "Call `run(x)` now.\n\n```\nconst x = 1;\nconst y = 2;\n```\n";
    expect(clicked(source, "run")).toBe(sourceAt(source, "run"));
    expect(clicked(source, "y =", 0, 0)).toBe(sourceAt(source, "y =", 0, 0));
  });

  it("a duplicate word: its context tells which", () => {
    const source = "**word** and word again.\n";
    expect(clicked(source, "word", 1)).toBe(sourceAt(source, "word", 1));
    expect(clicked(source, "word", 0)).toBe(sourceAt(source, "word", 0));
  });

  it("a repetition no context tells apart: the match nearest to where the click is in the block", () => {
    const source = `${Array.from({ length: 30 }, () => "na").join(" ")}\n`;
    expect(clicked(source, "na", 17)).toBe(sourceAt(source, "na", 17));
  });
});

describe("the mapping on its own", () => {
  it("not found: the proportional place in the block's lines", () => {
    const source = "intro\n\nabcdefghij\n";
    // Rendered text that the source doesn't hold (an entity, say), halfway through.
    expect(sourceOffset(source, { start: 3, end: 3 }, "xyz", "uvw", 0.5)).toBe(source.indexOf("abcdefghij") + 5);
  });

  it("found more than once: the nearest to the proportional place", () => {
    const source = "a\n\nab ab ab ab\n";
    const start = source.indexOf("ab ab");
    expect(sourceOffset(source, { start: 3, end: 3 }, "a", "b", 0)).toBe(start + 1);
    expect(sourceOffset(source, { start: 3, end: 3 }, "a", "b", 1)).toBe(start + 10);
  });

  it("only the block's lines are searched", () => {
    const source = "unique word\n\nanother unique line\n";
    expect(sourceOffset(source, { start: 3, end: 3 }, "uni", "que", 0.4)).toBe(source.indexOf("unique line") + 3);
  });

  for (const [what, html] of [
    ["a formula", '<span class="katex"><span>x²</span></span>'],
    ["a diagram", "<span data-diagram><span>flow</span></span>"],
    ["an embed", '<section class="k-embed"><span>embedded text</span></section>'],
  ] as const) {
    it(`inside ${what}: the start of the block's line`, () => {
      const source = "# Title\n\nbefore\nthe block's line\n";
      const block = document.createElement("div");
      block.dataset.line = "4";
      block.innerHTML = `text ${html}`;
      const node = block.lastElementChild!.querySelector("span, section")!.firstChild ?? block.lastElementChild!.firstChild!;
      expect(offsetAt(source, block, node, 1)).toBe(source.indexOf("the block's line"));
    });
  }
});
