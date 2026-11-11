// SPDX-License-Identifier: AGPL-3.0-or-later
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { cleanup, render, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { commentRanges } from "./commentRanges";
import { maskedBody } from "./comments";
import MarkdownView from "./MarkdownView";

interface Case {
  name: string;
  note: string;
  ranges: [number, number][];
  fairCopy: string;
  /** What the view shows, its whitespace collapsed. */
  view: string;
}

// The server checks its fair copy against the same cases (tests/Kumbu.Core.Tests/Publishing/FairCopyTests.cs).
const cases: Case[] = JSON.parse(readFileSync(join(import.meta.dirname, "comments.cases.json"), "utf8"));

afterEach(cleanup);

function shown(note: string) {
  const { container } = render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <MarkdownView source={maskedBody(note).body} mask={maskedBody(note).mask} links={[]} tasksDisabled={false} onToggleTask={() => {}} onCreateMissing={() => {}} />
    </MemoryRouter>,
  );
  return (container.textContent ?? "").replace(/\s+/g, " ").trim();
}

describe("reads comments as the shared cases say", () => {
  it.each(cases.map((c) => [c.name, c] as const))("%s", (_, c) => {
    expect(commentRanges(c.note)).toEqual(c.ranges);
    expect(shown(c.note)).toBe(c.view);
  });
});

it("hides a comment that opens in maths, its dollar left as written, as the fair copy does", async () => {
  const { container } = render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <MarkdownView source={"The rate is $a %% b$ today.\n\n%%\nShown"} links={[]} tasksDisabled={false} onToggleTask={() => {}} onCreateMissing={() => {}} />
    </MemoryRouter>,
  );
  await waitFor(() => expect(container).toHaveTextContent("Shown"));
  expect(container).toHaveTextContent("The rate is $a");
  expect(container).not.toHaveTextContent(/b|today/);
  expect(container.querySelector(".katex")).toBeNull();
});

// What the view hides, the fair copy leaves out: no word the view doesn't show is published.
describe("hides nothing the fair copy publishes", () => {
  // Character references count as what they stand for, which the view shows.
  const words = (text: string) => new Set(text.replace(/&#?\w+;/g, " ").match(/[\p{L}\p{N}]+/gu) ?? []);
  // Each text node and link address on its own: textContent runs a table's cells together.
  const visibleWords = (note: string) => {
    const { container } = render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <MarkdownView source={maskedBody(note).body} mask={maskedBody(note).mask} links={[]} tasksDisabled={false} onToggleTask={() => {}} onCreateMissing={() => {}} />
      </MemoryRouter>,
    );
    const parts: string[] = [...container.querySelectorAll("a[href]")].map((a) => a.getAttribute("href")!);
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) parts.push(n.textContent ?? "");
    cleanup();
    return words(parts.join(" "));
  };
  it.each(cases.map((c) => [c.name, c] as const))("%s", (_, c) => {
    const visible = visibleWords(c.note);
    const hidden = [...words(c.note)].filter((w) => !visible.has(w));
    expect(hidden.filter((w) => words(c.fairCopy).has(w))).toEqual([]);
  });
});

it("keeps a link whose text a comment empties, as the fair copy does", () => {
  const { container } = render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <MarkdownView source="[%%secret%%](https://x.org/path) after" links={[]} tasksDisabled={false} onToggleTask={() => {}} onCreateMissing={() => {}} />
    </MemoryRouter>,
  );
  expect(container.querySelector('a[href="https://x.org/path"]')).toHaveTextContent(/^$/);
  expect(container).not.toHaveTextContent("secret");
});

describe("reads comments in linear time", () => {
  // The fastest of three runs of a note of `size` characters.
  const time = (before: string, middle: string, after: string, size: number) => {
    const n = Math.floor(size / (before.length + after.length));
    const note = before.repeat(n) + middle + after.repeat(n);
    let best = Infinity;
    for (let run = 0; run < 3; run++) {
      const started = performance.now();
      commentRanges(note);
      best = Math.min(best, performance.now() - started);
    }
    return best;
  };

  // Eight times the text takes about eight times as long when linear, sixty-four when quadratic; timed at sizes
  // small enough that a quadratic reading fails here rather than running for minutes.
  it.each([
    ["%", "", ""],
    ["`", "", ""],
    ["```\n", "", ""],
    ["%%`", "", ""],
    ["%%\n```\n", "", ""],
    ["`%%", "", ""],
    ["<!", "<!---->", "---->"],
    ["%%<!--", "", ""],
  ])("%j %j %j", (before, middle, after) => {
    time(before, middle, after, 2 ** 14);
    const small = time(before, middle, after, 2 ** 14);
    // A slow small note fails before the large one could run for long.
    expect(small).toBeLessThan(1000);
    const large = time(before, middle, after, 2 ** 17);
    expect(large).toBeLessThan(5000);
    expect(large / Math.max(small, 1)).toBeLessThan(24);
  });
});
