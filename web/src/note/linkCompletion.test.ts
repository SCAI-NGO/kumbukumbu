// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { noteUrl } from "../api/paths";
import { filterRows, wordMatch } from "../palette/filter";
import { noteRows } from "../palette/rows";
import { headingLink } from "./outline";
import { blocksFor, completeLink, completeMarkdown, completeSubpath, embedsAsImage, headingsFor, linkedNote, linkQuery, linkTarget, markdownFragment, markdownPath, rankNotes } from "./linkCompletion";

describe("linkQuery", () => {
  it("finds an open [[ on the caret's line", () => {
    expect(linkQuery("See [[pla", 9)).toEqual({ kind: "note", start: 4, query: "pla" });
    expect(linkQuery("See [[", 6)).toEqual({ kind: "note", start: 4, query: "" });
    expect(linkQuery("a\nb [[x", 7)).toEqual({ kind: "note", start: 4, query: "x" });
    // An embed as a link: the `!` stays outside.
    expect(linkQuery("![[sh", 5)).toEqual({ kind: "note", start: 1, query: "sh" });
  });

  it("ignores a closed link, an alias, a heading's heading, and another line's [[", () => {
    expect(linkQuery("[[plan]] and", 12)).toBeNull();
    expect(linkQuery("[[plan|Pl", 9)).toBeNull();
    expect(linkQuery("[[plan#Goals#Ri", 15)).toBeNull();
    expect(linkQuery("[[plan\nnext", 11)).toBeNull();
    expect(linkQuery("[[plan#\nGo", 10)).toBeNull();
    expect(linkQuery("no link", 7)).toBeNull();
  });

  it("finds a heading of the named note, or of the open note after [[#", () => {
    expect(linkQuery("See [[Plan#", 11)).toEqual({ kind: "heading", start: 4, note: "Plan", query: "" });
    expect(linkQuery("[[Work/Plan#Go", 14)).toEqual({ kind: "heading", start: 0, note: "Work/Plan", query: "Go" });
    expect(linkQuery("[[#Intro", 8)).toEqual({ kind: "heading", start: 0, note: "", query: "Intro" });
    expect(linkQuery("![[Plan#Go", 10)).toEqual({ kind: "heading", start: 1, note: "Plan", query: "Go" });
  });

  it("finds a block after #^", () => {
    expect(linkQuery("[[Plan#^ab", 10)).toEqual({ kind: "block", start: 0, note: "Plan", query: "ab" });
    expect(linkQuery("[[#^", 4)).toEqual({ kind: "block", start: 0, note: "", query: "" });
  });

  it("finds a Markdown link's destination after ](, its [text] on the caret's line", () => {
    expect(linkQuery("See [x](pl", 10)).toEqual({ kind: "markdown", start: 8, query: "pl" });
    expect(linkQuery("[x](", 4)).toEqual({ kind: "markdown", start: 4, query: "" });
    expect(linkQuery("[x](/a/b.md#Gro", 15)).toEqual({ kind: "markdownHeading", start: 4, note: "/a/b.md", query: "Gro" });
    expect(linkQuery("[x](/a/My%20b.MD#", 17)).toEqual({ kind: "markdownHeading", start: 4, note: "/a/My%20b.MD", query: "" });
  });

  it("ignores a closed Markdown link, one without its [text] on the line, a wiki link's ]( and a # after no note", () => {
    expect(linkQuery("[x](pl) and", 11)).toBeNull();
    expect(linkQuery("[x\n](pl", 7)).toBeNull();
    expect(linkQuery("x](pl", 5)).toBeNull();
    expect(linkQuery("[[plan]](pl", 11)).toBeNull();
    expect(linkQuery("[x](plan#Go", 11)).toBeNull();
    expect(linkQuery("[x](#Go", 7)).toBeNull();
    expect(linkQuery("[x](/a.md#^id", 13)).toBeNull();
  });
});

describe("linkQuery in code", () => {
  const at = (text: string) => linkQuery(text.replace("|", ""), text.indexOf("|"));

  it("offers nothing inside a fenced code block, with either fence, nor on its fence line", () => {
    expect(at("```js\nfns[0](|\n```")).toBeNull();
    expect(at("~~~~\n```\nfns[0](|")).toBeNull();
    expect(at("```bash\nif [[ -f x|\n```")).toBeNull();
    expect(at("```[[pl|")).toBeNull();
  });

  it("offers nothing inside a code span, and takes no [[ or ]( from one before the caret", () => {
    expect(at("Call `a[0](|` here")).toBeNull();
    expect(at("``x [[pl|`` y")).toBeNull();
    expect(at("`[[x` and |")).toBeNull();
    expect(at("`[x](` and |")).toBeNull();
  });

  it("completes a link after a closed fence and after a code span", () => {
    expect(at("```\ncode\n```\nSee [[pl|")).toEqual({ kind: "note", start: 17, query: "pl" });
    expect(at("```\n[[x\n```\n[y](pl|")).toEqual({ kind: "markdown", start: 16, query: "pl" });
    expect(at("`a` [[pl|")).toEqual({ kind: "note", start: 4, query: "pl" });
  });

  it("completes a Markdown link straight after a word or ], as CommonMark reads one there", () => {
    expect(at('d["a"](|')).toEqual({ kind: "markdown", start: 7, query: "" });
    expect(at("[a][b](|")).toEqual({ kind: "markdown", start: 7, query: "" });
  });
});

describe("linkTarget", () => {
  const all = ["/W/plan.md", "/W/a/status.md", "/W/b/status.md", "/W/x/a/status.md", "/W/Notes/Idea.md"];

  it("is the name when no other note ends with it", () => {
    expect(linkTarget("/W/plan.md", all)).toBe("plan");
    expect(linkTarget("/W/Notes/Idea.md", all)).toBe("Idea");
  });

  it("takes as many folders as it needs to tell repeated names apart, ignoring case", () => {
    expect(linkTarget("/W/b/status.md", all)).toBe("b/status");
    expect(linkTarget("/W/x/a/status.md", all)).toBe("x/a/status");
    expect(linkTarget("/W/deep/plan.md", ["/W/Plan.md", "/W/deep/plan.md"])).toBe("deep/plan");
  });

  it("goes from the vault's root where even the whole path is the end of another", () => {
    expect(linkTarget("/W/a/status.md", all)).toBe("/a/status");
    expect(linkTarget("/W/Plan.md", ["/W/Plan.md", "/W/deep/plan.md"])).toBe("/Plan");
  });
});

describe("linkedNote", () => {
  const all = ["/W/Plan.md", "/W/a/status.md", "/W/b/status.md", "/W/x/a/status.md", "/W/shot.png", "/W/Notes/Idea.md"];

  it("is the note whose path in the vault ends with the link, ignoring case", () => {
    expect(linkedNote("plan", all, "/W/Notes/Idea.md")).toBe("/W/Plan.md");
    expect(linkedNote("NOTES/idea", all, "/W/Plan.md")).toBe("/W/Notes/Idea.md");
    expect(linkedNote("x/a/status", all, "/W/Plan.md")).toBe("/W/x/a/status.md");
    expect(linkedNote(" plan.md ", all, "/W/Notes/Idea.md")).toBe("/W/Plan.md");
  });

  it("takes a path from the vault's root as it stands", () => {
    expect(linkedNote("/a/status", all, "/W/Plan.md")).toBe("/W/a/status.md");
    expect(linkedNote("/status", all, "/W/Plan.md")).toBeNull();
  });

  it("takes the one in the open note's folder when several fit, else the first in text order", () => {
    expect(linkedNote("status", all, "/W/b/today.md")).toBe("/W/b/status.md");
    expect(linkedNote("status", all, "/W/Plan.md")).toBe("/W/a/status.md");
    expect(linkedNote("a/status", all, "/W/Plan.md")).toBe("/W/a/status.md");
  });

  it("takes a path with a folder below the open note's folder first, then from the vault's root, when several fit", () => {
    expect(linkedNote("a/status", ["/W/a/status.md", "/W/x/a/status.md"], "/W/x/today.md")).toBe("/W/x/a/status.md");
    expect(linkedNote("a/status", ["/W/a/status.md", "/W/X/a/status.md"], "/W/X/today.md")).toBe("/W/X/a/status.md");
    expect(linkedNote("a/status", ["/W/0/a/status.md", "/W/a/status.md"], "/W/b/today.md")).toBe("/W/a/status.md");
    expect(linkedNote("A/Status", ["/W/0/a/status.md", "/W/1/a/status.md"], "/W/b/today.md")).toBe("/W/0/a/status.md");
  });

  it("is none for a name no note ends with, an attachment, or a part of a name", () => {
    expect(linkedNote("nothing", all, "/W/Plan.md")).toBeNull();
    expect(linkedNote("shot.png", all, "/W/Plan.md")).toBeNull();
    expect(linkedNote("lan", all, "/W/Notes/Idea.md")).toBeNull();
    expect(linkedNote("", all, "/W/Plan.md")).toBeNull();
  });
});

describe("rankNotes", () => {
  const all = ["/W/roadmap.md", "/W/plan.md", "/W/projects/beta.md", "/W/old/plans.md", "/W/x/unplanned.md"];

  it("puts notes whose folder or name has a word starting with the query first, then those only containing it", () => {
    expect(rankNotes(all, "plan")).toEqual(["/W/plan.md", "/W/old/plans.md", "/W/x/unplanned.md"]);
    expect(rankNotes(all, "proj")).toEqual(["/W/projects/beta.md"]);
  });

  it("puts the note named as typed before notes only their folder matches", () => {
    const planning = ["/W/Plan.md", ...Array.from({ length: 10 }, (_, i) => `/W/Planning/A${i}.md`)];
    expect(rankNotes(planning, "plan")[0]).toBe("/W/Plan.md");
    expect(rankNotes(planning, "plan")).toHaveLength(8);
  });

  it("finds a note by its path as typed, with or without the vault's root", () => {
    const paths = ["/W/projects/plan.md", "/W/plan.md", "/W/old/projects.md"];
    expect(rankNotes(paths, "projects/pl")).toEqual(["/W/projects/plan.md"]);
    expect(rankNotes(paths, "/pro")).toEqual(["/W/old/projects.md", "/W/projects/plan.md"]);
  });

  it("leaves out notes no link can name: #, |, [ or ] in their path", () => {
    const odd = ["/W/C# notes.md", "/W/a|b.md", "/W/[draft] plan.md", "/W/x#y/plan.md", "/W/plain plan.md"];
    expect(rankNotes(odd, "")).toEqual(["/W/plain plan.md"]);
    expect(rankNotes(odd, "plan")).toEqual(["/W/plain plan.md"]);
  });

  it("lists every note for an empty query, in text order, at most eight", () => {
    expect(rankNotes(all, "")).toEqual(["/W/projects/beta.md", "/W/plan.md", "/W/old/plans.md", "/W/roadmap.md", "/W/x/unplanned.md"]);
    expect(rankNotes(Array.from({ length: 12 }, (_, i) => `/W/n${i}.md`), "")).toHaveLength(8);
  });
});

describe("one ranking", () => {
  const paths = ["/Work/Zeta plan.md", "/Work/Replan.md", "/Work/Plan b.md", "/Work/projects/Plan a.md", "/Work/plans/x.md", "/Work/old/roadmap plan.md", "/Work/road.md"];
  const note = (p: string) => ({ name: p.slice(p.lastIndexOf("/") + 1), path: p, size: 1, kind: "note", mediaType: "text/markdown" });
  const root = {
    name: "Work",
    path: "/Work",
    notes: paths.filter((p) => p.split("/").length === 3).map(note),
    folders: ["projects", "plans", "old"].map((f) => ({ name: f, path: `/Work/${f}`, folders: [], notes: paths.filter((p) => p.startsWith(`/Work/${f}/`)).map(note) })),
  };

  // The same notes match, and those where every word starts a word come first in both; within that the link list
  // takes the note's own name before its folder (8 rows shown, not the palette's 50).
  const startsFirst = (query: string, ranked: string[]) => {
    const match = wordMatch(query);
    return ranked.map((url) => match(paths.find((p) => noteUrl(p) === url)!.split("/").slice(2).join(" ").replace(/\.md$/, "")));
  };
  it.each(["plan", "PL RO", "road", "an", ""])("finds the notes for %j as the palette does, starts of words first", (query) => {
    const palette = filterRows(noteRows("Work", root), query, new Set()).map((r) => ("to" in r.run ? r.run.to : ""));
    const links = rankNotes(paths, query).map(noteUrl);
    expect([...links].sort()).toEqual([...palette].sort());
    const classes = startsFirst(query, links);
    expect(classes).toEqual([...classes].sort());
    expect(classes).toEqual(startsFirst(query, palette).sort());
  });
});

describe("completeLink", () => {
  it("replaces the typed part, and the ]] the toolbar left, with the whole link", () => {
    expect(completeLink("See [[pl", 8, { kind: "note", start: 4, query: "pl" }, "plan")).toEqual({ start: 4, end: 8, text: "[[plan]]", selStart: 12, selEnd: 12 });
    expect(completeLink("See [[pl]] x", 8, { kind: "note", start: 4, query: "pl" }, "plan")).toMatchObject({ start: 4, end: 10 });
  });
});

describe("headings and blocks of a note", () => {
  const note = ["---", "title: x", "---", "# Plan", "", "Why we do it. ^why", "", "## Goals", "", "- ship it ^ship", "- **test** it", "", "Long text", "on two lines", "", "^both", "", "## Risks"].join("\n");

  it("lists the headings in order, filtered by what is typed, ignoring case", () => {
    expect(headingsFor(note, "").map((h) => h.text)).toEqual(["Plan", "Goals", "Risks"]);
    expect(headingsFor(note, "GO").map((h) => h.text)).toEqual(["Goals"]);
    // A heading in the front matter's place is none: its lines are left out.
    expect(headingsFor("---\n# no\n---\ntext", "")).toEqual([]);
  });

  it("writes a heading as a link can hold it: its text as shown, without # | ^ [ ] and %%", () => {
    expect(headingLink("C# notes | [draft] ^x 50%% off")).toBe("C notes draft x 50 off");
    expect(headingLink("Goals")).toBe("Goals");
    expect(headingsFor("## The **road**map [[plan|Plan]]", "").map((h) => headingLink(h.text))).toEqual(["The roadmap Plan"]);
  });

  it("lists the block ids the note has, with the start of their text, filtered by id or text", () => {
    expect(blocksFor(note, "")).toEqual([
      { id: "why", text: "Why we do it." },
      { id: "ship", text: "ship it" },
      { id: "both", text: "Long text on two lines" },
    ]);
    expect(blocksFor(note, "SHI").map((b) => b.id)).toEqual(["ship"]);
    expect(blocksFor(note, "two lines").map((b) => b.id)).toEqual(["both"]);
    expect(blocksFor("a ^x\n\nb ^X", "")).toEqual([{ id: "x", text: "a" }]);
  });

  it("completes the link with the heading or block, and the ]] the toolbar left", () => {
    const at = { kind: "heading" as const, start: 4, note: "Plan", query: "Go" };
    expect(completeSubpath("See [[Plan#Go", 13, at, "Goals")).toEqual({ start: 4, end: 13, text: "[[Plan#Goals]]", selStart: 18, selEnd: 18 });
    expect(completeSubpath("See [[Plan#Go]] x", 13, at, "Goals")).toMatchObject({ end: 15 });
    expect(completeSubpath("![[#^s", 6, { kind: "block", start: 1, note: "", query: "s" }, "^ship")).toMatchObject({ start: 1, text: "[[#^ship]]" });
  });
});

describe("Markdown links", () => {
  it("writes a note's path from the vault's root, encoded as the server writes it", () => {
    expect(markdownPath("/Work/Folder/Note.md")).toBe("/Folder/Note.md");
    expect(markdownPath("/Work/My Folder/Plan (v2) #1.md")).toBe("/My%20Folder/Plan%20%28v2%29%20%231.md");
    expect(markdownPath("/Work/Größe 100%.md")).toBe("/Größe%20100%25.md");
    expect(markdownPath("/Work/Screenshots/shot.png")).toBe("/Screenshots/shot.png");
  });

  it("writes a heading after # as a link can hold it, encoded", () => {
    expect(markdownFragment("Next steps")).toBe("#Next%20steps");
    expect(markdownFragment("C# (later)")).toBe("#C%20%28later%29");
  });

  it("completes the destination, and a ) right after the caret", () => {
    const at = { kind: "markdown" as const, start: 8, query: "pl" };
    expect(completeMarkdown("See [x](pl", 10, at, "/Folder/plan.md")).toEqual({ start: 8, end: 10, text: "/Folder/plan.md)", selStart: 24, selEnd: 24 });
    expect(completeMarkdown("See [x](pl) and", 10, at, "/Folder/plan.md")).toMatchObject({ end: 11 });
  });
});

describe("attachments", () => {
  const all = ["/W/plan.md", "/W/Screenshots/shot.png", "/W/report.pdf", "/W/old/shot.png", "/W/shots.md"];

  it("completion offers images with an embed", () => {
    expect(rankNotes(all, "shot")).toEqual(["/W/old/shot.png", "/W/Screenshots/shot.png", "/W/shots.md"]);
    // Attachments are named with their extension; the same name in two folders takes a folder, as notes do.
    expect(linkTarget("/W/Screenshots/shot.png", all)).toBe("Screenshots/shot.png");
    expect(linkTarget("/W/report.pdf", all)).toBe("report.pdf");

    expect(completeLink("See [[sh", 8, { kind: "note", start: 4, query: "sh" }, "Screenshots/shot.png", true)).toEqual({
      start: 4,
      end: 8,
      text: "![[Screenshots/shot.png]]",
      selStart: 29,
      selEnd: 29,
    });
    // Typed as an embed already: the `!` stays as it is.
    expect(completeLink("See ![[sh]]", 9, { kind: "note", start: 5, query: "sh" }, "Screenshots/shot.png", true)).toMatchObject({ start: 5, end: 11, text: "[[Screenshots/shot.png]]" });
  });

  it("links a PDF without an embed", () => {
    expect(rankNotes(all, "rep")).toEqual(["/W/report.pdf"]);
    expect(completeLink("[[rep", 5, { kind: "note", start: 0, query: "rep" }, "report.pdf")).toMatchObject({ text: "[[report.pdf]]" });
    expect(embedsAsImage("/W/report.pdf")).toBe(false);
    expect(embedsAsImage("/W/Screenshots/shot.PNG")).toBe(true);
    expect(embedsAsImage("/W/plan.md")).toBe(false);
  });
});
