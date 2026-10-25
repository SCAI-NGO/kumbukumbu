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

  describe("an ambiguous link", () => {
    const ambiguous = () => show("[[plan]]", [link(1, "[[plan]]", "ambiguous", null, ["/V/a/plan.md", "/V/b/c/plan.md"])]);

    it("is a button with the candidate count, and opens nothing until tapped", () => {
      ambiguous();
      expect(screen.queryByRole("link")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: /plan/ })).toHaveTextContent("2 notes");
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    it("lists every candidate with its folder, and following one closes the dialog", async () => {
      ambiguous();
      await userEvent.click(screen.getByRole("button", { name: /plan/ }));
      const dialog = screen.getByRole("dialog", { name: "Several notes match “plan”" });
      const [first, second] = within(dialog).getAllByRole("link");
      expect(first).toHaveAttribute("href", "/notes/V/a/plan.md");
      expect(first).toHaveTextContent("V / a");
      expect(second).toHaveAttribute("href", "/notes/V/b/c/plan.md");
      expect(second).toHaveTextContent("V / b / c");
      expect(first).toHaveClass("min-h-11");
      // The end of a deep path is where candidates differ: the folder wraps, it is never cut.
      expect(within(first).getByText("V / a")).not.toHaveClass("truncate");

      await userEvent.click(second);
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    it("closes on Cancel and on Escape, giving focus back to the link", async () => {
      ambiguous();
      const opener = screen.getByRole("button", { name: /plan/ });
      await userEvent.click(opener);
      await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
      expect(opener).toHaveFocus();

      await userEvent.click(opener);
      await userEvent.keyboard("{Escape}");
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });
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

  describe("lists", () => {
    const bulleted = (li: HTMLElement) => li.closest("ul")!.classList.contains("list-disc") && !li.classList.contains("list-none");

    it("keeps the bullets of ordinary items in a list that also has a task", () => {
      show("- eins\n\n- [x] zwei");
      const [eins, zwei] = screen.getAllByRole("listitem");
      expect(bulleted(eins)).toBe(true);
      expect(bulleted(zwei)).toBe(false);
      expect(screen.getByRole("checkbox", { name: "zwei" })).toBeInTheDocument();
    });

    it("gives only the ordinary item of a tight mixed list a bullet", () => {
      show("- a\n- [ ] b");
      const [a, b] = screen.getAllByRole("listitem");
      expect(bulleted(a)).toBe(true);
      expect(bulleted(b)).toBe(false);
    });

    it("shows a list of only tasks without bullets", () => {
      show("- [ ] a\n- [x] b");
      const list = screen.getByRole("list");
      expect(list).toHaveClass("list-none");
      expect(list).not.toHaveClass("list-disc");
      for (const li of screen.getAllByRole("listitem")) expect(bulleted(li)).toBe(false);
    });
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

describe("Callouts", () => {
  const callouts = () => [...document.querySelectorAll<HTMLElement>("[data-callout]")];

  it("renders `> [!type] Title` as a box with its title and body, and a plain quote as a quote", () => {
    show("> [!warning] Mind the **gap**\n> Body text\n> more\n\n> just a quote");
    const [box] = callouts();
    expect(box.tagName).toBe("ASIDE");
    expect(box).toHaveAttribute("data-callout", "warning");
    expect(box.querySelector("[data-callout-title]")).toHaveTextContent("Mind the gap");
    expect(within(box).getByText("gap").tagName).toBe("STRONG");
    expect(box).toHaveTextContent(/Body text\s*more/);
    expect(box).not.toHaveTextContent("[!warning]");
    expect(document.querySelector("blockquote")).toHaveTextContent("just a quote");
    expect(callouts()).toHaveLength(1);
  });

  it("names an untitled callout after its type, and gives aliases and unknown types a known look", () => {
    show("> [!tip]\n> Use it.\n\n> [!caution] Careful\n\n> [!whatever] Odd\n\n> [!QUOTE]\n> Said.");
    expect(callouts().map((c) => c.dataset.callout)).toEqual(["tip", "warning", "note", "quote"]);
    expect(callouts()[0].querySelector("[data-callout-title]")).toHaveTextContent(/^Tip$/);
    expect(callouts()[3].querySelector("[data-callout-title]")).toHaveTextContent(/^Quote$/);
  });

  it("folds `+` (open) and `-` (shut) callouts as details, toggled by their title", async () => {
    show("> [!note]+ Open one\n> Inside A\n\n> [!danger]- Shut one\n> Inside B");
    const [open, shut] = callouts();
    expect(open.tagName).toBe("DETAILS");
    expect(open).toHaveAttribute("open");
    expect(shut).not.toHaveAttribute("open");
    expect(shut).toHaveAttribute("data-callout", "danger");
    const summary = shut.querySelector("summary")!;
    expect(summary).toHaveTextContent("Shut one");
    await userEvent.click(summary);
    expect(shut).toHaveAttribute("open");
  });

  it("resolves wiki links in a callout's title and body on their lines", () => {
    show("> [!info] See [[plan]]\n> and [[roadmap]]", [link(1, "[[plan]]", "note", "/W/plan.md"), link(2, "[[roadmap]]", "note", "/W/roadmap.md")]);
    expect(screen.getByRole("link", { name: "plan" })).toHaveAttribute("href", "/notes/W/plan.md");
    expect(screen.getByRole("link", { name: "roadmap" })).toHaveAttribute("href", "/notes/W/roadmap.md");
  });
});
