// SPDX-License-Identifier: AGPL-3.0-or-later
// Words search over the copy (offline mode design §2a, §3a): sections as the server's search splits them
// (Kumbu.Core.Tests Search/ChunkerTests), tags as it reads them (Tags/TagReaderTests), every word of the query, title
// hits first.
import { describe, expect, test } from "vitest";
import { MirrorSearch, NoSuchTagError } from "../src/read/search";
import { sectionsOf, snippetOf } from "../src/search/sections";
import { carries, normaliseTag, tagsOf } from "../src/search/tags";
import { prepare, searchWords } from "../src/search/words";
import { MirrorCopies } from "../src/store/mirror";
import { FakeBrowser } from "./fakeStore";

describe("sections", () => {
  test("are split by headings, frontmatter left out, as the server's search splits them", () => {
    const sections = sectionsOf("/Demo/plan.md", "---\ntags: x\n---\nIntro text.\n\n# Plan\nTop.\n## Goals\nShip it.\n\n## Risks\n\n");
    expect(sections.map((s) => [s.address, s.startLine, s.endLine, s.text, s.context])).toEqual([
      ["", 3, 4, "Intro text.", "plan"],
      ["#Plan", 5, 7, "# Plan\nTop.", "plan > Plan"],
      ["#Plan#Goals", 7, 9, "## Goals\nShip it.", "plan > Plan > Goals"],
      ["#Plan#Risks", 10, 11, "## Risks", "plan > Plan > Risks"],
    ]);
  });

  test("a long section comes in parts of at most 2,000 characters, cut between paragraphs", () => {
    const paragraph = Array(150).fill("word").join(" ");
    const parts = sectionsOf("/Demo/long.md", "# Long\n" + Array(6).fill(paragraph).join("\n\n") + "\n");
    expect(parts.length).toBeGreaterThanOrEqual(3);
    for (const p of parts) {
      expect(p.text.length).toBeLessThanOrEqual(2000);
      expect(p.address).toBe("#Long");
    }
    for (const p of parts.slice(1)) expect(p.text.split("\n")[0]!.trim()).not.toBe("");
  });

  test("addresses number siblings of the same text and escape #; code holds no heading", () => {
    const sections = sectionsOf("/Demo/n.md", "# A\n## Notes\none\n## Notes\ntwo\n# Q\\#A\n```\n# not a heading\n```\n# x[2]\nend\n");
    expect(sections.map((s) => s.address)).toEqual(["#A", "#A#Notes[1]", "#A#Notes[2]", "#Q\\#A", "#x\\[2]"]);
  });

  test("reads headings as before", () => {
    const address = (line: string) => sectionsOf("/Demo/n.md", `${line}\nbody\n`)[0]!.address;
    expect(["# A", "## A ##", "# A #  ", "#\tA\t#\t", "# A#", "## B ## x", "#   A  b  ", "   # A", "# #", "# a \\#"].map(address)).toEqual(
      ["#A", "#A", "#A", "#A", "#A\\#", "#B \\#\\# x", "#A  b", "#A", "#\\#", "#a \\#"],
    );
    expect(["#", "#  ", "###### ######"].map(address)).toEqual(["#", "#", "#\\#\\#\\#\\#\\#\\#"]);
    expect(["####### A", "    # A", "#tag", "A # B"].map(address)).toEqual(["", "", "", ""]);
  });

  test("the snippet is the line with the whole query, else the one with most of its words, else the start", () => {
    const [section] = sectionsOf("/Demo/n.md", "# Pump\nThe pump runs.\nPumpe 50 is red and blue.\nblue only\n");
    expect(snippetOf(section!, "pumpe 5")).toBe("Pumpe 50 is red and blue.");
    expect(snippetOf(section!, "blue red")).toBe("Pumpe 50 is red and blue.");
    expect(snippetOf(section!, "zebra")).toBe("The pump runs. Pumpe 50 is red and blue. blue only");
    const [long] = sectionsOf("/Demo/n.md", "x".repeat(200) + " needle\n");
    expect(snippetOf(long!, "needle")).toBe("x".repeat(160) + "…");
  });
});

describe("tags", () => {
  test.each([
    ["#project/kumbu and #idea", ["project/kumbu", "idea"]],
    ["---\ntags: [work, '#plan']\n---\ntext", ["work", "plan"]],
    ["---\ntags:\n  - a\n  - b\n---\n", ["a", "b"]],
    ["---\ntags: one, two three\n---\n", ["one", "two", "three"]],
    ["`#code` and [[#heading]] and %% #hidden %% and <!-- #gone --> #kept", ["kept"]],
    ["```\n#fenced\n```\n\n    #indented\n#123 a#b #ok/", ["ok"]],
    ["- item\n    #nested", ["nested"]],
  ])("%j", (content, expected) => expect(tagsOf(content)).toEqual(expected));

  test("strips a tag's trailing slashes, in the text, the front matter and a query", () => {
    expect(tagsOf("#plan/ and #a//b and #work///\n")).toEqual(["plan", "a//b", "work"]);
    expect(tagsOf("---\ntags: [plan/, 'idea//']\n---\n")).toEqual(["plan", "idea"]);
    expect([normaliseTag("#plan/"), normaliseTag("a/b//"), normaliseTag("///"), normaliseTag("#")]).toEqual(["plan", "a/b", null, null]);
  });

  test("a tag carries its nested ones, ignoring case", () => {
    expect(carries(tagsOf("#Project/Kumbu"), "project")).toBe(true);
    expect(carries(tagsOf("#Project/Kumbu"), "project/kumbu")).toBe(true);
    expect(carries(tagsOf("#projects"), "project")).toBe(false);
  });
});

describe("words", () => {
  const notes = [
    prepare("/Work/Pump.md", "# Pump\nSee the maintenance list.\n"),
    prepare("/Work/log.md", "# Monday\nThe pump was serviced, pump oil changed.\n# Tuesday\nOil only.\n# Wednesday\nThe pump.\n"),
    prepare("/Work/other.md", "Nothing here.\n"),
    prepare("/Work/heads.md", "# pump\n"),
  ];

  test("finds the sections holding every word, title hits first, then by matches, ignoring case", () => {
    expect(searchWords(notes, "PUMP", 20).map((h) => [h.path, h.address, h.reason])).toEqual([
      ["/Work/Pump.md", "#Pump", "title"],
      ["/Work/log.md", "#Monday", "exact"],
      ["/Work/log.md", "#Wednesday", "exact"],
    ]);
    expect(searchWords(notes, "oil pump", 20).map((h) => [h.address, h.reason, h.snippet])).toEqual([
      ["#Monday", "all words", "The pump was serviced, pump oil changed."],
    ]);
  });

  test("gives lines 1-based, as the server's results, and at most `max`", () => {
    const [hit] = searchWords(notes, "oil only", 20);
    expect(hit).toMatchObject({ path: "/Work/log.md", address: "#Tuesday", startLine: 3, endLine: 4, snippet: "Oil only." });
    expect(searchWords(notes, "pump", 1)).toHaveLength(1);
    expect(searchWords(notes, "zebra", 20)).toEqual([]);
  });

  test("the whole query found in a line is found though its words aren't whole ones", () => {
    const hits = searchWords([prepare("/Work/p.md", "Pumpe 50 läuft.\n")], "pumpe 5", 20);
    expect(hits.map((h) => h.reason)).toEqual(["exact"]);
  });
});

describe("the copy", () => {
  async function copy() {
    const browser = new FakeBrowser();
    browser.slots.vaults = [{ id: "v1", tag: "t", canWrite: true, pending: false }];
    const mirror = await new MirrorCopies(browser.tab()).create({ userId: "u", username: "Ada", password: "pw", label: "L", idleMs: 60_000 });
    const vault = await mirror.adopt("v1");
    await vault.setMeta({ name: "Work" });
    const files = vault.files();
    await files.writeIf("plan.md", null, "# Plan\nThe pump #project/kumbu\n");
    await files.writeIf("_archive/old.md", null, "An old pump. #project\n");
    await files.writeIf(".hidden/x.md", null, "pump\n");
    return { browser, mirror, vault, files };
  }

  test("searches the held vaults' notes, the archive only when asked", async () => {
    const { mirror } = await copy();
    const search = new MirrorSearch(mirror);
    expect((await search.find("pump", { max: 20 })).map((h) => h.path)).toEqual(["/Work/plan.md"]);
    expect((await search.find("pump", { max: 20, archive: true })).map((h) => h.path)).toEqual(["/Work/_archive/old.md", "/Work/plan.md"]);
  });

  test("narrows to the notes with a tag or one nested below it; a tag no note carries says so", async () => {
    const { mirror } = await copy();
    const search = new MirrorSearch(mirror);
    expect((await search.find("pump", { max: 20, tag: "project/kumbu", archive: true })).map((h) => h.path)).toEqual(["/Work/plan.md"]);
    expect((await search.find("pump", { max: 20, tag: "Project", archive: true })).map((h) => h.path)).toEqual(["/Work/_archive/old.md", "/Work/plan.md"]);
    await expect(search.find("pump", { max: 20, tag: "nothing" })).rejects.toThrow(new NoSuchTagError("nothing"));
  });

  test("a note whose records were damaged on the device is left out, not the search", async () => {
    const { browser, mirror } = await copy();
    for (const key of [...browser.storage.records.keys()].filter((k) => k.includes("b/"))) browser.storage.records.delete(key);
    expect(await new MirrorSearch(mirror).find("pump", { max: 20 })).toEqual([]);
  });

  test("reads a note again only once its text changed", async () => {
    const { mirror, vault, files } = await copy();
    const search = new MirrorSearch(mirror);
    expect(await search.find("valve", { max: 20 })).toEqual([]);
    expect(await files.writeIf("plan.md", vault.entry("plan.md")!.local!.version, "# Plan\nThe valve.\n")).toBe(true);
    expect((await search.find("valve", { max: 20 })).map((h) => h.path)).toEqual(["/Work/plan.md"]);
  });
});
