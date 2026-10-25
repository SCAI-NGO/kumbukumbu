// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { LinkView } from "./wikiLinks";
import MarkdownView from "./MarkdownView";

const link = (line: number, raw: string, kind: string, path: string | null = null, candidates: string[] = []): LinkView => ({
  line,
  raw,
  target: raw.replace(/^!?\[\[/, "").replace(/(#|\|).*$|\]\]$/g, ""),
  kind,
  path,
  candidates,
});

function show(
  source: string,
  links: LinkView[] = [],
  handlers: Partial<{ onToggleTask(line: number): void; onCreateMissing(target: string): void }> = {},
  highlight: { lines: Set<number> | "all"; writer: string } | null = null,
) {
  render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <MarkdownView
        source={source}
        links={links}
        tasksDisabled={false}
        onToggleTask={handlers.onToggleTask ?? vi.fn()}
        onCreateMissing={handlers.onCreateMissing ?? vi.fn()}
        highlight={highlight}
      />
    </MemoryRouter>,
  );
}

describe("MarkdownView", () => {
  it("renders H2 in mono, uppercase, in the accent colour", () => {
    show("## Goals\n\ntext");
    const h2 = screen.getByRole("heading", { level: 2, name: "Goals" });
    expect(h2).toHaveClass("font-mono", "uppercase", "text-accent-text");
  });

  it("links a resolved wiki link to its note, showing the alias or the target", () => {
    show("See [[plan]] and [[roadmap|the road]] or [[plan#Goals]].", [
      link(1, "[[plan]]", "note", "/Work/projects/plan.md"),
      link(1, "[[roadmap|the road]]", "note", "/Work/roadmap.md"),
      link(1, "[[plan#Goals]]", "note", "/Work/projects/plan.md"),
    ]);
    expect(screen.getByRole("link", { name: "plan" })).toHaveAttribute("href", "/notes/Work/projects/plan.md");
    expect(screen.getByRole("link", { name: "the road" })).toHaveAttribute("href", "/notes/Work/roadmap.md");
    expect(screen.getByRole("link", { name: "plan › Goals" })).toHaveAttribute("href", "/notes/Work/projects/plan.md");
  });

  it("maps each link to the API's result by line and occurrence, not by its name", () => {
    // The same text on two lines can resolve differently (the API is the authority, e.g. after a move).
    show("[[x]] and [[x]]\n\n[[x]]", [
      link(1, "[[x]]", "note", "/V/a/x.md"),
      link(1, "[[x]]", "not_found"),
      link(3, "[[x]]", "note", "/V/b/x.md"),
    ]);
    const links = screen.getAllByRole("link", { name: "x" });
    expect(links.map((a) => a.getAttribute("href"))).toEqual(["/notes/V/a/x.md", "/notes/V/b/x.md"]);
    expect(screen.getByRole("button", { name: "Create the note “x”" })).toBeInTheDocument();
  });

  it("shows a missing link dashed and muted, and creates it on tap", async () => {
    const onCreateMissing = vi.fn();
    show("Next: [[new idea]]", [link(1, "[[new idea]]", "not_found")], { onCreateMissing });
    const missing = screen.getByRole("button", { name: "Create the note “new idea”" });
    expect(missing).toHaveClass("decoration-dashed", "text-muted");
    await userEvent.click(missing);
    expect(onCreateMissing).toHaveBeenCalledWith("new idea");
  });

  it("marks an ambiguous link with its candidate count and links nowhere", () => {
    show("[[plan]]", [link(1, "[[plan]]", "ambiguous", null, ["/V/a/plan.md", "/V/b/plan.md"])]);
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
    expect(screen.getByText("plan")).toBeInTheDocument();
    expect(screen.getByText("2 notes")).toBeInTheDocument();
  });

  it("never turns a link into a cross-vault one: without the API's answer it stays text", () => {
    show("[[Other/plan]]", []);
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(screen.getByText("Other/plan")).toBeInTheDocument();
  });

  it("leaves wiki links in code alone", () => {
    show("`[[plan]]`\n\n```\n[[plan]]\n```", []);
    expect(screen.getAllByText("[[plan]]")).toHaveLength(2);
  });

  it("toggles a task by its source line", async () => {
    const onToggleTask = vi.fn();
    show("# Plan\n\n- [ ] write\n- [x] read", [], { onToggleTask });
    const read = screen.getByRole("checkbox", { name: "read" });
    expect(read).toHaveAttribute("aria-checked", "true");
    await userEvent.click(screen.getByRole("checkbox", { name: "write" }));
    expect(onToggleTask).toHaveBeenCalledWith(3);
    await userEvent.click(read);
    expect(onToggleTask).toHaveBeenLastCalledWith(4);
  });

  it("shows raw HTML as text and never renders it", () => {
    show('<script>alert(1)</script>\n\nA <b onclick="x()">bold</b> word');
    expect(document.querySelector("script, b")).toBeNull();
    expect(screen.getByText("<script>alert(1)</script>")).toBeInTheDocument();
  });

  it("opens external links in a new tab without opener or referrer", () => {
    show("[site](https://example.org) and [bad](javascript:alert(1))");
    const site = screen.getByRole("link", { name: "site" });
    expect(site).toHaveAttribute("target", "_blank");
    expect(site).toHaveAttribute("rel", "noopener noreferrer");
    expect(screen.getByText("bad").closest("a")?.getAttribute("href") ?? "").not.toMatch(/javascript/);
  });

  it("does not load images; it names them instead", () => {
    show("![a chart](https://tracker.example/pixel.png)");
    expect(document.querySelector("img")).toBeNull();
    expect(within(document.body).getByText(/a chart/)).toBeInTheDocument();
  });

  it("maps the server's real answer: embeds, headings of this note and aliases with a heading", () => {
    // As GET /api/links answered for this text on the local stack (frontmatter lines left empty, as NoteView does).
    const source = "\n\n\n# T\n- [ ] a [[zzmissing]] and `[[code]]` [[zzmissing]]\n[[t9probe#H|al]] ![[img.png]] [[#Self]]";
    show(source, [
      { line: 5, raw: "[[zzmissing]]", target: "zzmissing", kind: "not_found", path: null, candidates: [] },
      { line: 5, raw: "[[zzmissing]]", target: "zzmissing", kind: "not_found", path: null, candidates: [] },
      { line: 6, raw: "[[t9probe#H|al]]", target: "t9probe", kind: "note", path: "/Demo/t9probe.md", candidates: [] },
      { line: 6, raw: "![[img.png]]", target: "img.png", kind: "attachment", path: null, candidates: [] },
      { line: 6, raw: "[[#Self]]", target: "", kind: "self", path: "/Demo/t9probe.md", candidates: [] },
    ]);
    expect(screen.getAllByRole("button", { name: "Create the note “zzmissing”" })).toHaveLength(2);
    expect(screen.getByText("[[code]]")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "al" })).toHaveAttribute("href", "/notes/Demo/t9probe.md");
    expect(screen.getByText("img.png")).not.toHaveAttribute("href");
    expect(screen.getByText("Self")).toBeInTheDocument();
    expect(screen.getAllByRole("link")).toHaveLength(1);
  });

  it("matches links whose backslash escapes Markdown took out, as in an Obsidian table", () => {
    // The server's raw keeps the backslashes; the rendered text has none.
    show("| Note | Why |\n| --- | --- |\n| [[plan\\|the plan]] | [[a\\_b]] |", [
      { line: 3, raw: "[[plan\\|the plan]]", target: "plan", kind: "note", path: "/V/plan.md", candidates: [] },
      { line: 3, raw: "[[a\\_b]]", target: "a\\_b", kind: "not_found", path: null, candidates: [] },
    ]);
    expect(screen.getByRole("link", { name: "the plan" })).toHaveAttribute("href", "/notes/V/plan.md");
    expect(screen.getByRole("button", { name: "Create the note “a\\_b”" })).toBeInTheDocument();
  });

  it("tints the blocks whose source lines changed, with the writer's name", () => {
    show("# Title\n\nkept\n\nnew text\n\n- one\n- two changed", [], {}, { lines: new Set([5, 8]), writer: "liz" });
    const tinted = [...document.querySelectorAll("[data-changed]")];
    expect(tinted.map((e) => e.textContent)).toEqual(["new text", "two changed"]);
    expect(tinted.every((e) => e.getAttribute("data-writer") === "liz")).toBe(true);
  });

  it("tints every block of a note the agent wrote from the start", () => {
    show("# Title\n\ntext\n\n- [ ] task", [], {}, { lines: "all", writer: "liz" });
    expect([...document.querySelectorAll("[data-changed]")].map((e) => e.tagName)).toEqual(["H1", "P", "LI"]);
  });

  it("tints nothing without highlights", () => {
    show("# Title\n\ntext");
    expect(document.querySelector("[data-changed]")).toBeNull();
  });
});
