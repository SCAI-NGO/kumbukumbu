// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { complete } from "./complete";
import { consoleCommands, shCommands } from "./help";
import { commandTable, resolvePath } from "./paths";
import { tokenise } from "./tokenise";

const tree: Record<string, string[]> = {
  "/": ["Work/", "Garten Arbeit/", "garten/"],
  "/Work": ["_archive/", "Meeting Notes.md", "Meeting Plan.md", "plan.md", "projects/", "pictures/", "it's.md", "a#b.png", "q\"u'o.md", "shot.png"],
  "/Work/projects": ["alpha/", "alpha.md", "My Folder/"],
  "/Work/projects/My Folder": ["Inner Note.md", "deep/"],
  "/Garten Arbeit": ["Beete.md"],
  "/Empty": ["x.md"],
  "/Empty/_archive": ["old.md"],
};
const list = async (folder: string) => tree[folder] ?? null;

/** Completes at the end of `line` (or at `cursor`) and gives the line with the insert applied. */
async function tab(line: string, cwd = "/Work", cursor = line.length) {
  const result = await complete(line, cursor, cwd, list);
  const applied = result.insert === null ? line : line.slice(0, result.replaceFrom) + result.insert + line.slice(result.replaceTo);
  return { ...result, applied };
}

describe("a unique match", () => {
  it("completes a file with a trailing space and a folder with a slash", async () => {
    expect((await tab("cat pl")).applied).toBe("cat plan.md ");
    expect((await tab("cat proj")).applied).toBe("cat projects/");
  });

  it("completes the vaults at /", async () => {
    expect((await tab("ls /Wo")).applied).toBe("ls /Work/");
    expect((await tab("ls /", "/")).matches).toEqual(["Work/", "Garten Arbeit/", "garten/"]);
    expect((await tab("ls Wo", "/")).applied).toBe("ls Work/");
  });

  it("reports the replaced range", async () => {
    const result = await tab("cat  pl");
    expect([result.replaceFrom, result.replaceTo]).toEqual([5, 7]);
  });

  it("completes the word before the cursor", async () => {
    expect((await tab("cat pl | wc", "/Work", 6)).applied).toBe("cat plan.md  | wc");
  });
});

describe("several matches", () => {
  it("completes to the common prefix and lists the matches", async () => {
    const result = await tab("cat p");
    expect(result.matches).toEqual(["plan.md", "projects/", "pictures/"]);
    expect(result.insert).toBeNull();
    expect((await tab("cat pr")).applied).toBe("cat projects/");
  });

  it("inserts a common prefix that goes beyond what is typed", async () => {
    expect((await tab("cat Meet")).matches).toEqual(["Meeting Notes.md", "Meeting Plan.md"]);
    expect((await tab("ls /G", "/")).insert).toBe("/Garten\\ Arbeit/");
    expect((await tab("cat Meeting", "/Work")).insert).toBe("Meeting\\ ");
  });

  it("gives no match and no insert for nothing", async () => {
    expect(await tab("cat zzz")).toMatchObject({ matches: [], insert: null });
  });

  it("offers nothing for a folder that can't be listed, or an option", async () => {
    expect(await tab("cat nope/x")).toMatchObject({ matches: [], insert: null });
    expect(await tab("rg --t")).toMatchObject({ matches: [], insert: null });
  });
});

describe("relative paths", () => {
  it("works with .. and .", async () => {
    expect((await tab("cat ../Work/pl")).applied).toBe("cat ../Work/plan.md ");
    expect((await tab("cat ./proj")).applied).toBe("cat ./projects/");
    expect((await tab("ls ../", "/Work/projects")).matches).toContain("plan.md");
    expect((await tab("cd ..", "/Work/projects")).applied).toBe("cd ../");
    expect((await tab("cd ..", "/")).matches).toEqual([]);
  });
});

describe("cd", () => {
  it("completes folders and vaults only", async () => {
    expect((await tab("cd p")).matches).toEqual(["projects/", "pictures/"]);
    expect((await tab("cd pl")).matches).toEqual([]);
    expect((await tab("cd /", "/")).matches).toEqual(["Work/", "Garten Arbeit/", "garten/"]);
  });
});

describe("commands", () => {
  it("completes the first word, also after a pipe", async () => {
    expect((await tab("sem")).applied).toBe("semsearch ");
    expect((await tab("cat a.md | he")).matches).toEqual(["head", "help"]);
    expect((await tab("cat a.md | tai")).applied).toBe("cat a.md | tail ");
    expect((await tab("ca")).matches).toEqual(["calendar", "cat"]);
    expect((await tab("ex")).applied).toBe("exit ");
  });

  it("offers the console's own commands, and none the console refuses", async () => {
    expect((await tab("ls")).matches).toEqual(["ls", "lsa", "lscp"]);
    expect((await tab("p")).matches).toContain("pwd");
    expect((await tab("rm")).matches).toEqual([]);
    expect((await tab("mk")).matches).toEqual([]);
  });

  it("keeps the lists in step with the console", () => {
    expect([...shCommands].sort()).toEqual(Object.keys(commandTable).sort());
    expect(consoleCommands).toEqual(["cd", "pwd", "clear", "exit", "l", "la", "lsa", "p"]);
  });

  it("completes a path word after a pipe's command, not as a command", async () => {
    expect((await tab("cat a.md | rg x pl")).applied).toBe("cat a.md | rg x plan.md ");
  });
});

describe("_archive", () => {
  it("is offered only once its underscore is typed, and only where the vault has one", async () => {
    expect((await tab("ls /Work/")).matches).not.toContain("_archive/");
    expect((await tab("ls /Work/_")).applied).toBe("ls /Work/_archive/");
    expect((await tab("ls /Empty/_")).matches).toEqual([]);
    expect((await tab("ls /Work/projects/_")).matches).toEqual([]);
  });
});

describe("names are escaped so the tokeniser reads them back", () => {
  const read = (line: string) => tokenise(line)!.at(-1)!.at(-1)!;
  const same = (word: string, expected: string) =>
    expect(resolvePath(word, "/Work").replace(/\/$/, "")).toBe(expected.replace(/\/$/, ""));

  it.each([
    ["cat Meeting\\ N", "/Work/Meeting Notes.md"],
    ["cat it", "/Work/it's.md"],
    ["cat a#", "/Work/a#b.png"],
    ["cat q", "/Work/q\"u'o.md"],
    ["cat projects/My", "/Work/projects/My Folder/"],
    ["cat /Garten", "/Garten Arbeit/"],
    ["cat projects/My\\ Folder/In", "/Work/projects/My Folder/Inner Note.md"],
  ])("%s", async (line, expected) => {
    const { applied } = await tab(line);
    same(read(applied.trimEnd()), expected);
    expect(tokenise(applied)).not.toBeNull();
  });

  it("uses backslashes, not quotes", async () => {
    expect((await tab("cat Meeting\\ N")).insert).toBe("Meeting\\ Notes.md ");
    expect((await tab("cat it")).insert).toBe("it\\'s.md ");
    expect((await tab("cat a#")).insert).toBe("a#b.png ");
    expect((await tab("cat q")).insert).toBe('q\\"u\\\'o.md ');
    expect((await tab("cat projects/My")).insert).toBe("projects/My\\ Folder/");
  });

  it("goes on after a folder without a quote in the way", async () => {
    const { applied } = await tab("cat projects/My");
    const second = await tab(applied + "In");
    expect(second.applied).toBe("cat projects/My\\ Folder/Inner\\ Note.md ");
    same(read(second.applied.trimEnd()), "/Work/projects/My Folder/Inner Note.md");
  });

  it("never leaves a quote open on a partial completion", async () => {
    const result = await tab("cat Meet");
    expect(result.insert).toBe("Meeting\\ ");
    expect(tokenise(result.applied + "N")![0]).toEqual(["cat", "Meeting N"]);
  });

  it("completes inside a quote already opened, closing it only on a unique file", async () => {
    expect((await tab("cat 'Meeting P")).applied).toBe("cat 'Meeting Plan.md' ");
    expect((await tab('cat "Meeting P')).applied).toBe('cat "Meeting Plan.md" ');
    expect((await tab("cat 'Meet")).applied).toBe("cat 'Meeting ");
    expect((await tab("cat 'projects/My")).applied).toBe("cat 'projects/My Folder/");
    expect((await tab("cat 'it")).applied).toBe(`cat "it's.md" `);
    expect((await tab("cat projects/'My Folder'/In")).applied).toBe("cat 'projects/My Folder/Inner Note.md' ");
  });
});

describe("a chosen match", () => {
  it("completes as the only match would, for a path and a command", async () => {
    const meeting = await complete("cat Mee", 7, "/Work", list, "Meeting Plan.md");
    expect(meeting).toMatchObject({ matches: ["Meeting Plan.md"], insert: "Meeting\\ Plan.md " });
    expect((await complete("cat pro", 7, "/Work", list, "projects/")).insert).toBe("projects/");
    expect((await complete("l", 1, "/Work", list, "ls")).insert).toBe("ls ");
  });
});

describe("an empty stage", () => {
  it("offers nothing, rather than every command", async () => {
    expect(await tab("")).toMatchObject({ matches: [], insert: null });
    expect(await tab("cat a.md | ")).toMatchObject({ matches: [], insert: null });
    expect(await tab("cat a.md |")).toMatchObject({ matches: [], insert: null });
  });
});
