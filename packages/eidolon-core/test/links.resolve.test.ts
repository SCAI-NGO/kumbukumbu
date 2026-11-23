// SPDX-License-Identifier: AGPL-3.0-or-later
// Kumbu.Core.Tests Links/LinkResolverTests, AliasResolutionTests and AttachmentLinkTests: the server's resolution rules.
import { describe, expect, test } from "vitest";
import { parseLinks } from "../src/links/parse";
import { LinkResolver } from "../src/links/resolve";

/** Where a wikilink to `target` leads, in the C# tests' words. */
function answer(resolver: LinkResolver, source: string, target: string): string {
  const r = resolver.resolve(source, target);
  const text = r.kind === "note" ? r.target!
    : r.kind === "attachment" ? (r.target === null ? "attachment" : `attachment ${r.target}`)
    : r.kind === "ambiguous" ? `ambiguous: ${[...r.candidates].sort().join(", ")}`
    : r.kind;
  return r.viaAlias ? `${text} (alias)` : text;
}

const NOTES = ["/W/projects/kumbu.md", "/W/inbox.md", "/W/a/plan.md", "/W/b/plan.md", "/W/Überlegungen.md", "/Other/inbox.md", "/W/v1.2 notes.md"];

const RESOLVES: Array<[string, string, string]> = [
  ["/W/inbox.md", "kumbu", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "projects/kumbu", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "projects/kumbu.md", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "projects/kumbu.MD", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "/projects/kumbu", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "KUMBU", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "  kumbu ", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "überlegungen", "/W/Überlegungen.md"],
  ["/W/inbox.md", "Überlegungen", "/W/Überlegungen.md"],
  ["/W/inbox.md", "Überlegungen".normalize("NFD"), "/W/Überlegungen.md"],
  ["/W/inbox.md", "v1.2 notes", "/W/v1.2 notes.md"],
  ["/W/a/plan.md", "plan", "/W/a/plan.md"],
  ["/W/b/x.md", "plan", "/W/b/plan.md"],
  ["/W/inbox.md", "a/plan", "/W/a/plan.md"],
  ["/W/a/plan.md", "../b/plan", "/W/b/plan.md"],
  ["/W/projects/kumbu.md", "./kumbu", "/W/projects/kumbu.md"],
  ["/W/inbox.md", "plan", "ambiguous: /W/a/plan.md, /W/b/plan.md"],
  ["/W/inbox.md", "nope", "not_found"],
  ["/W/inbox.md", "./kumbu", "not_found"],
  ["/W/inbox.md", "/kumbu", "not_found"],
  ["/W/inbox.md", "../../../etc/passwd", "not_found"],
  ["/W/inbox.md", "../Other/inbox", "not_found"],
  ["/W/inbox.md", "jects/kumbu", "not_found"],
  ["/W/inbox.md", "diagram.png", "attachment"],
  ["/W/inbox.md", "report.docx", "attachment"],
  ["/W/inbox.md", "", "self"],
];

describe("notes", () => {
  const resolver = new LinkResolver("W", NOTES, null);

  test.each(RESOLVES)("from %s, [[%s]] → %s", (source, target, expected) => expect(answer(resolver, source, target)).toBe(expected));

  test("[[#Heading]] is the note itself", () => {
    expect(resolver.resolve("/W/inbox.md", "")).toEqual({ kind: "self", target: "/W/inbox.md", candidates: [], viaAlias: false });
  });

  test("stays inside the vault", () => expect(answer(resolver, "/W/projects/kumbu.md", "inbox")).toBe("/W/inbox.md"));

  test("an ambiguous link lists every candidate, a resolved one too", () => {
    expect(resolver.resolve("/W/inbox.md", "plan").candidates).toEqual(["/W/a/plan.md", "/W/b/plan.md"]);
    expect(resolver.resolve("/W/a/x.md", "plan")).toEqual({ kind: "note", target: "/W/a/plan.md", candidates: ["/W/a/plan.md", "/W/b/plan.md"], viaAlias: false });
  });

  test("ß is not SS", () => {
    const r = new LinkResolver("W", ["/W/Straße.md"], null);
    expect(answer(r, "/W/x.md", "STRASSE")).toBe("not_found");
    expect(answer(r, "/W/x.md", "straße")).toBe("/W/Straße.md");
  });
});

// Ruling 1: a path with a folder is tried relative to the source's folder, then as an exact vault path, then as a suffix.
describe("paths with a folder", () => {
  const resolver = new LinkResolver("W", ["/W/Work/plan.md", "/W/x/Work/plan.md", "/W/x/sub/plan.md", "/W/sub/plan.md"], null);
  test.each([
    ["/W/inbox.md", "Work/plan", "/W/Work/plan.md"],
    ["/W/x/note.md", "sub/plan.md", "/W/x/sub/plan.md"],
    ["/W/x/note.md", "Work/plan", "/W/x/Work/plan.md"],
    ["/W/sub/note.md", "Work/plan", "/W/Work/plan.md"],
    ["/W/sub/note.md", "sub/plan", "/W/sub/plan.md"],
    // Follows Obsidian: no /W/x/Work/Work/plan.md, so the exact vault path wins over the suffix /W/x/Work/plan.md.
    ["/W/x/Work/inbox.md", "Work/plan", "/W/Work/plan.md"],
  ])("from %s, [[%s]] → %s", (source, target, expected) => expect(answer(resolver, source, target)).toBe(expected));
});

describe("folder markers", () => {
  test.each([".vaultkeep", "Drafts/.vaultkeep", "/Drafts/.vaultkeep"])("[[%s]] is never a link target", (target) => {
    const resolver = new LinkResolver("W", ["/W/inbox.md"], ["/W/Drafts/.vaultkeep", "/W/Drafts/shot.png"]);
    expect(resolver.resolve("/W/Drafts/n.md", target).kind).toBe("not_found");
  });
});

// An archived note never makes a living note's link ambiguous; only when every candidate is archived does it lead there.
describe("the archive", () => {
  const resolver = new LinkResolver("W", [
    "/W/inbox.md", "/W/Projects/Overview.md", "/W/_archive/Projects/Overview.md",
    "/W/a/Plans/Budget.md", "/W/_archive/Plans/Budget.md", "/W/_archive/Old.md",
    "/W/_archive/a/Gone.md", "/W/_archive/b/Gone.md", "/W/a/Twice.md", "/W/b/Twice.md", "/W/_archive/Twice.md",
    "/W/Inbox/_archive/Deep.md", "/W/Notes/Deep.md", "/W/_archive/Specs/Pump.md", "/W/b/_archive/Specs/Pump.md",
  ], null);
  test.each([
    ["/W/inbox.md", "Overview", "/W/Projects/Overview.md"],
    ["/W/inbox.md", "Projects/Overview", "/W/Projects/Overview.md"],
    ["/W/inbox.md", "Plans/Budget", "/W/a/Plans/Budget.md"],
    ["/W/inbox.md", "Budget", "/W/a/Plans/Budget.md"],
    ["/W/inbox.md", "Old", "/W/_archive/Old.md"],
    ["/W/inbox.md", "Gone", "ambiguous: /W/_archive/a/Gone.md, /W/_archive/b/Gone.md"],
    ["/W/inbox.md", "Twice", "ambiguous: /W/a/Twice.md, /W/b/Twice.md"],
    // An exact link leads into the archive as anywhere else.
    ["/W/inbox.md", "/_archive/Projects/Overview", "/W/_archive/Projects/Overview.md"],
    ["/W/_archive/Projects/x.md", "./Overview", "/W/_archive/Projects/Overview.md"],
    // A folder _archive further down is an ordinary one.
    ["/W/inbox.md", "Deep", "ambiguous: /W/Inbox/_archive/Deep.md, /W/Notes/Deep.md"],
    // The folder-part preferences come first.
    ["/W/inbox.md", "_archive/Specs/Pump", "/W/_archive/Specs/Pump.md"],
    // An archived note's links resolve as anywhere.
    ["/W/_archive/Projects/x.md", "Overview", "/W/_archive/Projects/Overview.md"],
    ["/W/_archive/x.md", "Twice", "/W/_archive/Twice.md"],
    ["/W/_archive/a/x.md", "Twice", "ambiguous: /W/_archive/Twice.md, /W/a/Twice.md, /W/b/Twice.md"],
    ["/W/_archive/a/x.md", "Budget", "ambiguous: /W/_archive/Plans/Budget.md, /W/a/Plans/Budget.md"],
  ])("from %s, [[%s]] → %s", (source, target, expected) => expect(answer(resolver, source, target)).toBe(expected));
});

/** A resolver over /W/Projects/Kumbukumbu.md (aliases Kumbu and K²), /W/Diary/day.md and `more`. */
function withAliases(more: Array<[string, string[]]> = [], attachments: string[] | null = null): LinkResolver {
  const notes: Array<[string, string[]]> = [["/W/Projects/Kumbukumbu.md", ["Kumbu", "K²"]], ["/W/Diary/day.md", []], ...more];
  return new LinkResolver("W", notes.map(([p]) => p), attachments, new Map(notes.filter(([, a]) => a.length > 0)));
}

describe("aliases", () => {
  test.each([
    ["/W/Diary/day.md", "Kumbu", "/W/Projects/Kumbukumbu.md (alias)"],
    ["/W/Diary/day.md", "kumbu", "/W/Projects/Kumbukumbu.md (alias)"],
    ["/W/Diary/day.md", "K²", "/W/Projects/Kumbukumbu.md (alias)"],
    ["/W/Diary/day.md", "Kumbukumbu", "/W/Projects/Kumbukumbu.md"],
    ["/W/Diary/day.md", "Kumbu.md", "not_found"],
    ["/W/Diary/day.md", "/Kumbu", "not_found"],
    ["/W/Diary/day.md", "./Kumbu", "not_found"],
    ["/W/Diary/day.md", "Projects/Kumbu", "not_found"],
    ["/W/Projects/Kumbukumbu.md", "Kumbu", "/W/Projects/Kumbukumbu.md (alias)"],
  ])("only for a bare name: from %s, [[%s]] → %s", (source, target, expected) => expect(answer(withAliases(), source, target)).toBe(expected));

  test("a Markdown link names a path, never an alias", () => {
    const [link] = parseLinks("[k](Kumbu)");
    expect(withAliases().resolve("/W/Diary/day.md", link!).kind).toBe("not_found");
    const [wiki] = parseLinks("[[Kumbu]]");
    expect(withAliases().resolve("/W/Diary/day.md", wiki!).viaAlias).toBe(true);
  });

  test("a note's name beats an alias", () => {
    expect(answer(withAliases([["/W/Ideas/Kumbu.md", []]]), "/W/Diary/day.md", "Kumbu")).toBe("/W/Ideas/Kumbu.md");
  });

  test("an attachment's name beats an alias", () => {
    expect(answer(withAliases([["/W/Notes/Screens.md", ["shot.png"]]], ["/W/shot.png"]), "/W/Diary/day.md", "shot.png")).toBe("attachment /W/shot.png");
  });

  test("an alias never takes a name of a kind the vault keeps as attachments", () => {
    expect(answer(withAliases([["/W/Shots.md", ["shot.png"]]]), "/W/Diary/day.md", "shot.png")).toBe("attachment");
  });

  test("a name ambiguity stays one", () => {
    expect(answer(withAliases([["/W/a/Kumbu.md", []], ["/W/b/Kumbu.md", []]]), "/W/Diary/day.md", "Kumbu")).toBe("ambiguous: /W/a/Kumbu.md, /W/b/Kumbu.md");
  });

  test("two notes sharing an alias are ambiguous, and the source's folder settles it", () => {
    const resolver = withAliases([["/W/People/Kumbu Team.md", ["K²"]]]);
    expect(answer(resolver, "/W/Diary/day.md", "K²")).toBe("ambiguous: /W/People/Kumbu Team.md, /W/Projects/Kumbukumbu.md (alias)");
    expect(answer(resolver, "/W/People/x.md", "K²")).toBe("/W/People/Kumbu Team.md (alias)");
  });

  test("a living note beats an archived one by alias", () => {
    const resolver = withAliases([["/W/_archive/Old.md", ["K²"]]]);
    expect(answer(resolver, "/W/Diary/day.md", "K²")).toBe("/W/Projects/Kumbukumbu.md (alias)");
    expect(answer(resolver, "/W/_archive/Sub/x.md", "K²")).toBe("ambiguous: /W/Projects/Kumbukumbu.md, /W/_archive/Old.md (alias)");
  });

  test("an alias that looks like a file name resolves", () => {
    expect(answer(withAliases([["/W/Notes/Release.md", ["v1.0"]]]), "/W/Diary/day.md", "v1.0")).toBe("/W/Notes/Release.md (alias)");
  });

  test("aliases of notes not given, or in another vault, don't count", () => {
    const resolver = new LinkResolver("W", ["/W/Diary/day.md", "/Other/x.md"], null, new Map([["/W/Gone.md", ["Bob"]], ["/Other/x.md", ["Carol"]]]));
    expect(answer(resolver, "/W/Diary/day.md", "Bob")).toBe("not_found");
    expect(answer(resolver, "/W/Diary/day.md", "Carol")).toBe("not_found");
  });

  test("an alias equal to the note's own name changes nothing", () => {
    const resolver = new LinkResolver("W", ["/W/Projects/Kumbukumbu.md", "/W/b/Kumbukumbu.md"], null, new Map([["/W/Projects/Kumbukumbu.md", ["kumbukumbu"]]]));
    expect(answer(resolver, "/W/Diary/day.md", "Kumbukumbu")).toBe("ambiguous: /W/Projects/Kumbukumbu.md, /W/b/Kumbukumbu.md");
  });

  test.each(RESOLVES)("without aliases nothing changes: from %s, [[%s]]", (source, target) => {
    expect(new LinkResolver("W", NOTES, null, new Map()).resolve(source, target)).toEqual(new LinkResolver("W", NOTES, null).resolve(source, target));
  });

  test("many notes sharing many aliases index in linear time", () => {
    const shared = Array.from({ length: 100 }, (_, i) => `a${i}`);
    const notes = Array.from({ length: 5000 }, (_, i) => `/W/n${i}.md`);
    const started = Date.now();
    const resolver = new LinkResolver("W", notes, null, new Map(notes.map((n) => [n, shared])));
    expect(Date.now() - started).toBeLessThan(3000);
    expect(resolver.resolve("/W/x/y.md", "a1").kind).toBe("ambiguous");
  });
});

describe("attachments", () => {
  const resolver = new LinkResolver("W", ["/W/inbox.md", "/W/Screens/notes.md", "/W/shot.md"],
    ["/W/Screens/shot.png", "/W/Docs/My report.pdf", "/W/a/dup.png", "/W/b/dup.png", "/Other/elsewhere.png"]);
  const note = (target: string) => (target.startsWith("/W/") ? `note ${target}` : target);

  test.each([
    ["/W/inbox.md", "shot.png", "attachment /W/Screens/shot.png"],
    ["/W/inbox.md", "SHOT.PNG", "attachment /W/Screens/shot.png"],
    ["/W/inbox.md", "Screens/shot.png", "attachment /W/Screens/shot.png"],
    ["/W/inbox.md", "/Screens/shot.png", "attachment /W/Screens/shot.png"],
    ["/W/Screens/notes.md", "./shot.png", "attachment /W/Screens/shot.png"],
    ["/W/inbox.md", "My report.pdf", "attachment /W/Docs/My report.pdf"],
    // Without its extension the name is a note's.
    ["/W/inbox.md", "shot", "note /W/shot.md"],
    ["/W/inbox.md", "dup.png", "ambiguous: /W/a/dup.png, /W/b/dup.png"],
    ["/W/a/x.md", "dup.png", "attachment /W/a/dup.png"],
    // A file that isn't there leads nowhere, whatever its kind.
    ["/W/inbox.md", "missing.png", "not_found"],
    ["/W/inbox.md", "elsewhere.png", "not_found"],
    ["/W/inbox.md", "report.docx", "not_found"],
  ])("from %s, [[%s]] → %s", (source, target, expected) => expect(note(answer(resolver, source, target))).toBe(expected));

  test.each([
    ["shot.png", "attachment /W/Screens/shot.png"],
    ["Shots/old.png", "attachment /W/a/Shots/old.png"],
    ["lone.png", "attachment /W/_archive/lone.png"],
    ["gone.png", "ambiguous: /W/_archive/a/gone.png, /W/_archive/b/gone.png"],
    ["/_archive/Screens/shot.png", "attachment /W/_archive/Screens/shot.png"],
  ])("an embed prefers what is outside the archive: [[%s]] → %s", (target, expected) => {
    const archived = new LinkResolver("W", ["/W/inbox.md"], [
      "/W/Screens/shot.png", "/W/_archive/Screens/shot.png", "/W/a/Shots/old.png", "/W/_archive/Shots/old.png",
      "/W/_archive/lone.png", "/W/_archive/a/gone.png", "/W/_archive/b/gone.png",
    ]);
    expect(answer(archived, "/W/inbox.md", target)).toBe(expected);
  });

  test.each([
    ["data.csv", "note /W/data.csv.md"],
    ["Makefile", "attachment /W/src/Makefile"],
    ["src/Makefile", "attachment /W/src/Makefile"],
    // A name that looks like a file's is a note's where no file is stored at it, and where a note carries it.
    ["Project v1.2", "note /W/Project v1.2.md"],
    ["README", "note /W/README.md"],
    ["v1.2", "not_found"],
    ["nowhere", "not_found"],
  ])("a link names a file only where one is stored: [[%s]] → %s", (target, expected) => {
    const files = new LinkResolver("W", ["/W/inbox.md", "/W/data.csv.md", "/W/Project v1.2.md", "/W/README.md", "/W/v1.2/notes.md"],
      ["/W/data.csv", "/W/src/Makefile", "/W/v1.2/shot.png"]);
    expect(note(answer(files, "/W/inbox.md", target))).toBe(expected);
  });

  test.each([
    // A note comes first: a file stored under a note's name takes no link over.
    ["Ideas", "note /W/Ideas.md"],
    ["README", "note /W/README.md"],
    ["Old name", "note /W/Ideas.md (alias)"],
    // Only where no note matches does a stored file of that name answer; a format's extension names the format first.
    ["data.csv", "attachment /W/data.csv"],
    ["Makefile", "attachment /W/Makefile"],
    ["shot.png", "attachment /W/shot.png"],
    ["report.docx", "not_found"],
  ])("a note wins over a file of its name: [[%s]] → %s", (target, expected) => {
    const files = new LinkResolver("W", ["/W/inbox.md", "/W/Ideas.md", "/W/README.md", "/W/shot.png.md"],
      ["/W/old/Ideas", "/W/a/README", "/W/b/README", "/W/Old name", "/W/data.csv", "/W/Makefile", "/W/shot.png"],
      new Map([["/W/Ideas.md", ["Old name"]]]));
    expect(note(answer(files, "/W/inbox.md", target))).toBe(expected);
  });

  test("a Markdown embed resolves as a wiki embed does", () => {
    const [link] = parseLinks("![s](Screens/shot.png)");
    expect(resolver.resolve("/W/inbox.md", link!)).toMatchObject({ kind: "attachment", target: "/W/Screens/shot.png" });
  });
});

test("resolves a vault of 20,000 notes in time", () => {
  const notes = Array.from({ length: 20_000 }, (_, i) => `/W/Area ${i % 50}/Project ${i % 400}/Note ${i}.md`);
  const started = Date.now();
  const resolver = new LinkResolver("W", notes, null);
  for (let i = 0; i < 20_000; i++) resolver.resolve("/W/inbox.md", `Note ${i}`);
  expect(Date.now() - started).toBeLessThan(5000);
  expect(answer(resolver, "/W/inbox.md", "Project 7/Note 407")).toBe("/W/Area 7/Project 7/Note 407.md");
});
