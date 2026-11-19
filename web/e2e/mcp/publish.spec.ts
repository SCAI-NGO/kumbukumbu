// SPDX-License-Identifier: AGPL-3.0-or-later
// Publish 2.0 through `sh publish` (docs/superpowers/specs/2026-11-18-publish-bundles-design.md; PR #235), read back as
// a reader without an account reads it (POST /api/s, ReaderApi.cs).
//
// Promises in scope, and the test that covers each:
// 1. `--depth N --dry-run` lists what would go and what stays, with the reasons in words, and publishes nothing
//    (decisions 1, 2; "The flows") → "publish: --dry-run lists what would go and what stays …".
// 2. A marked note is never taken and the walk doesn't go through it: a note reached only through it stays out too
//    (decision 3, "The walk") → the dry-run test and "publish: add takes what the dry run listed …".
// 3. `add` publishes what the dry run showed and prints it; links to a note left out are plain text, links inside the
//    copy stay links (decisions 2, 5) → "publish: add takes what the dry run listed …".
// 4. `--without <note>` leaves the note out ("taken out by you") and what is reached only through it; `update` keeps
//    the one-off exclusion (decision 2) → "publish: --without leaves a note out …".
// 5. A mark set later, a move into a marked folder and a removal take the note out of the copy at once, along with the
//    notes the stored edges reach only through it; a note still reached another way stays; the links to it become
//    plain text; `publish ls` counts the notes (decision 3, "A mark set later", "Notes behind it leave too")
//    → "publish: marking, moving or removing a note takes it out of the copy …".
// 6. The reader never learns a note was left out: the copy's ids have no gaps, no link names a missing id, and nothing
//    of a left-out note but its link's text is in the copy (anti-goal 3; "Numbering without gaps") → checked by
//    `holdsNothingOf` in tests 2 to 4.
// 7. Agents need "May publish"; the depth goes from 0 to 5 (decisions 1, 8) → "publish: a key without May publish …".
// Left out: the limits of 500 notes and 64 MB (decision 9): not cheap to reach on the stack, covered by xUnit; the
// activity entries (decision 10) and the bar above the notes (decision 7): the web app, not this project; the 409 of a
// changed set (HTTP only; `sh` walks and publishes in one step).
import { request } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, encodePath, guarded, Mcp } from "../support/api";
import { baseURL } from "../support/env";
import { reserveReaderRead } from "../support/session";
import { expect, redact, test } from "./fixtures";

type ReaderNote = {
  id: number;
  title: string;
  markdown: string;
  depth: number;
};
type ReaderView = { title: string; markdown: string; notes?: ReaderNote[] };

const reasons = {
  unpublishable: "marked unpublishable",
  behind: "only reached through a note that stays out",
  without: "taken out by you",
};
const shareCode = /^[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){7}$/;
const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** The notes of a bundle below `/Work/<folder>/`, named with the folder's random part, as links resolve vault-wide. */
function bundle(folder: string) {
  const id = folder.slice(-6);
  const name = (title: string) => `${title} ${id}`;
  return {
    name,
    path: (title: string, under = "") => `/Work/${folder}/${under}${name(title)}.md`,
    link: (title: string) => `[[${name(title)}]]`,
    /** A text only this note holds: the copy must never carry it once the note is out. */
    body: (title: string) => `body-of-${title.toLowerCase()}-${id}`,
  };
}

/** The lines of `publish add|update`: the notes listed under `notes:`, and `left out:` as path → reason. */
function listed(text: string) {
  const lines = text.split("\n");
  const block = (head: string) => {
    const at = lines.indexOf(head);
    if (at < 0) return [];
    const rest = lines.slice(at + 1);
    const end = rest.findIndex((l) => !l.startsWith("  "));
    return (end < 0 ? rest : rest.slice(0, end)).map((l) => l.slice(2));
  };
  return {
    notes: block("notes:"),
    leftOut: Object.fromEntries(block("left out:").map((l) => l.split(/ {2}(?=[^ ])/) as [string, string])),
  };
}

/** The link `publish add` printed, checked by its parts; never in a message. */
function linkOf(text: string) {
  const link = /^link \(shown once\): (\S+)$/m.exec(text)?.[1];
  expect(link !== undefined, `publish add printed a link:\n${redact(text)}`).toBe(true);
  const url = new URL(link!);
  expect(`${url.origin}${url.pathname}`, "the reader page").toBe(`${baseURL}/s`);
  expect(shareCode.test(url.hash.slice(1)), "the fragment is a share code").toBe(true);
  return link!;
}

/** The copy as a reader without an account gets it, with the code from the link's fragment. */
async function readCopy(link: string): Promise<ReaderView> {
  const anon = await request.newContext({
    baseURL,
    extraHTTPHeaders: { Origin: baseURL },
  });
  try {
    await reserveReaderRead();
    // Guarded: a call log would show the code in the body.
    const response = await guarded(() => anon.post("/api/s", { data: { code: new URL(link).hash.slice(1) } }), "POST /api/s");
    expect(response.status(), "the reader's read").toBe(200);
    return (await response.json()) as ReaderView;
  } finally {
    await anon.dispose();
  }
}

/** The copy's titles in walk order; a single note's is the copy's own. */
const titles = (view: ReaderView) => (view.notes ?? [{ title: view.title }]).map((n) => n.title);
const sorted = (view: ReaderView) => [...titles(view)].sort();

/**
 * Nothing tells the reader a note was left out: ids 0, 1, 2 … without gaps, every link within the copy names a note
 * it holds, and none of the left-out notes' own texts is anywhere in it.
 */
function holdsNothingOf(view: ReaderView, texts: string[]) {
  const notes = view.notes ?? [];
  expect(
    notes.map((n) => n.id),
    "the ids have no gaps",
  ).toEqual(notes.map((_, i) => i));
  const ids = new Set(notes.map((n) => n.id));
  const targets = [view.markdown, ...notes.map((n) => n.markdown)].flatMap((m) => [...m.matchAll(/#kn-(\d+)/g)].map((x) => Number(x[1])));
  expect(
    targets.filter((t) => !ids.has(t)),
    "links to notes the copy doesn't hold",
  ).toEqual([]);
  const all = JSON.stringify(view);
  expect(
    texts.filter((t) => all.includes(t)),
    "texts of left-out notes in the copy",
  ).toEqual([]);
}

test.describe("publish", () => {
  let alice: Api;
  /** Alice's agent with "May publish" in Work. */
  let key: Mcp;

  test.beforeAll(async () => {
    alice = await Api.as("alice");
    key = await Mcp.create(alice, `e2e publish ${randomBytes(3).toString("hex")}`, { Work: "write" }, ["Work"]);
  });

  /** Clean-ups a test registers before it writes: they run after it even when it timed out, which a finally doesn't. */
  let cleanUps: (() => Promise<void>)[] = [];
  const afterTest = (cleanUp: () => Promise<void>) => void cleanUps.push(cleanUp);
  test.afterEach(async () => {
    for (const cleanUp of cleanUps.reverse()) await cleanUp();
    cleanUps = [];
  });

  test.afterAll(async () => {
    await key?.dispose();
    await alice?.dispose();
  });

  async function write(path: string, content: string) {
    const written = await key.call("write", { path, content });
    expect(written.isError, written.text).toBe(false);
  }

  async function mark(path: string) {
    const response = await alice.send("PUT", `/api/unpublishable/${encodePath(path)}`);
    expect(response.status(), `marking ${path} unpublishable`).toBe(204);
  }

  /**
   * Start → Alpha → Deep, Start → Beta → Gamma, Start → Secret (marked) → Hidden: at depth 2 the copy takes five
   * notes; Secret stays out for its mark, Hidden for being reached only through it. Cleaned up after the test.
   */
  async function seedTree(folder: string) {
    const b = bundle(folder);
    const all = ["Start", "Alpha", "Beta", "Secret", "Deep", "Gamma", "Hidden"].map((t) => b.path(t));
    afterTest(() => cleanUp(b.path("Start"), [b.path("Secret")], all));
    await write(b.path("Start"), `# Start\n\n${b.link("Alpha")} and ${b.link("Secret")} and ${b.link("Beta")}.\n`);
    await write(b.path("Alpha"), `# Alpha\n\n${b.link("Deep")}\n`);
    await write(b.path("Beta"), `# Beta\n\n${b.link("Gamma")}\n`);
    await write(b.path("Secret"), `# Secret\n\n${b.body("Secret")}\n\n${b.link("Hidden")}\n`);
    for (const title of ["Deep", "Gamma", "Hidden"]) await write(b.path(title), `# ${title}\n\n${b.body(title)}\n`);
    await mark(b.path("Secret"));
    return b;
  }

  /** Withdraws, lifts the marks and removes the notes, whatever the test got to. */
  async function cleanUp(start: string, marks: string[], paths: string[], folders: string[] = []) {
    await key.sh(`publish rm "${start}"`);
    for (const m of marks) await alice.send("DELETE", `/api/unpublishable/${encodePath(m)}`);
    await key.sh(`rm ${paths.map((p) => `"${p}"`).join(" ")}`);
    for (const f of folders) await key.sh(`rm "${f}"`);
  }

  test("publish: --dry-run lists what would go and what stays, with reasons, and publishes nothing @publish", async ({ folder }) => {
    const b = await seedTree(folder);
    const dry = await key.sh(`publish add "${b.path("Start")}" --depth 2 --dry-run`);
    expect(dry.isError, dry.text).toBe(false);
    expect(dry.text.split("\n")[0]).toBe(`would publish ${b.path("Start")} with 5 notes; nothing was published`);
    expect(dry.text).not.toMatch(/^link /m);
    expect(listed(dry.text)).toEqual({
      notes: ["Start", "Alpha", "Beta", "Deep", "Gamma"].map((t) => b.path(t)),
      leftOut: {
        [b.path("Hidden")]: reasons.behind,
        [b.path("Secret")]: reasons.unpublishable,
      },
    });

    // One link deep, Hidden isn't reached at all, so it isn't listed.
    const one = await key.sh(`publish add "${b.path("Start")}" --depth 1 --dry-run`);
    expect(one.text.split("\n")[0]).toBe(`would publish ${b.path("Start")} with 3 notes; nothing was published`);
    expect(listed(one.text)).toEqual({
      notes: ["Start", "Alpha", "Beta"].map((t) => b.path(t)),
      leftOut: { [b.path("Secret")]: reasons.unpublishable },
    });

    // Nothing went out.
    expect(await key.sh(`publish ls /Work/${folder}`)).toEqual({
      text: `no published notes below /Work/${folder}`,
      isError: false,
    });
  });

  test("publish: add takes what the dry run listed, never a marked note nor what lies behind it @publish", async ({ folder }) => {
    const b = await seedTree(folder);
    const dry = listed((await key.sh(`publish add "${b.path("Start")}" --depth 2 --dry-run`)).text);
    const added = await key.sh(`publish add "${b.path("Start")}" --depth 2`);
    expect(added.isError, redact(added.text)).toBe(false);
    expect(added.text.split("\n")[0]).toMatch(
      new RegExp(`^published ${escape(b.path("Start"))} with 5 notes as a copy of \\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC$`),
    );
    expect(listed(added.text), "what went out is what the dry run listed").toEqual(dry);
    const link = linkOf(added.text);

    expect((await key.sh(`publish ls /Work/${folder}`)).text).toMatch(new RegExp(`^${escape(b.path("Start"))} {2}.+ UTC {2}older: no {2}notes: 5$`));

    const copy = await readCopy(link);
    expect(titles(copy)).toEqual(["Start", "Alpha", "Beta", "Deep", "Gamma"]);
    expect(copy.notes!.map((n) => n.depth)).toEqual([0, 1, 1, 2, 2]);
    // Links inside the copy stay links; the one to the marked note is its text, with no marker.
    expect(copy.notes![0].markdown).toBe(`[${b.name("Alpha")}](#kn-1) and ${b.name("Secret")} and [${b.name("Beta")}](#kn-2).\n`);
    holdsNothingOf(copy, [b.body("Secret"), b.body("Hidden"), folder]);
  });

  test("publish: --without leaves a note out and what is reached only through it; update keeps it out @publish", async ({ folder }) => {
    const b = await seedTree(folder);
    const expected = {
      notes: ["Start", "Alpha", "Deep"].map((t) => b.path(t)),
      leftOut: {
        [b.path("Beta")]: reasons.without,
        [b.path("Gamma")]: reasons.behind,
        [b.path("Hidden")]: reasons.behind,
        [b.path("Secret")]: reasons.unpublishable,
      },
    };
    const dry = await key.sh(`publish add "${b.path("Start")}" --depth 2 --without "${b.path("Beta")}" --dry-run`);
    expect(dry.text.split("\n")[0]).toBe(`would publish ${b.path("Start")} with 3 notes; nothing was published`);
    expect(listed(dry.text)).toEqual(expected);

    const added = await key.sh(`publish add "${b.path("Start")}" --depth 2 --without "${b.path("Beta")}"`);
    expect(added.isError, redact(added.text)).toBe(false);
    expect(listed(added.text)).toEqual(expected);
    const link = linkOf(added.text);
    expect(titles(await readCopy(link))).toEqual(["Start", "Alpha", "Deep"]);

    // Update copy walks again and keeps the one-off exclusion.
    const updated = await key.sh(`publish update "${b.path("Start")}"`);
    expect(updated.isError, redact(updated.text)).toBe(false);
    expect(updated.text.split("\n")[0]).toMatch(
      new RegExp(`^updated the published copy of ${escape(b.path("Start"))} \\(\\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC\\); the link stays the same$`),
    );
    expect(listed(updated.text)).toEqual(expected);
    const copy = await readCopy(link);
    expect(titles(copy)).toEqual(["Start", "Alpha", "Deep"]);
    holdsNothingOf(copy, [b.body("Gamma"), b.body("Secret"), b.body("Hidden"), folder]);
  });

  test("publish: marking, moving or removing a note takes it out of the copy with the notes only it reached @publish", async ({ folder }) => {
    const b = bundle(folder);
    const closed = `/Work/${folder}/Closed`;
    // Start → Alpha, Beta, Gamma; Alpha → AlphaChild, Shared; Beta → BetaChild; Gamma → GammaChild, Shared.
    const all = ["Start", "Alpha", "Gamma", "AlphaChild", "BetaChild", "GammaChild", "Shared"].map((t) => b.path(t));
    const beta = [b.path("Beta"), b.path("Beta", "Closed/")];
    afterTest(() => cleanUp(b.path("Start"), [b.path("Alpha"), closed], [...all, ...beta], [closed]));
    await write(b.path("Start"), `# Start\n\n${b.link("Alpha")}, ${b.link("Beta")}, ${b.link("Gamma")}\n`);
    await write(b.path("Alpha"), `# Alpha\n\n${b.body("Alpha")} ${b.link("AlphaChild")} ${b.link("Shared")}\n`);
    await write(b.path("Beta"), `# Beta\n\n${b.body("Beta")} ${b.link("BetaChild")}\n`);
    await write(b.path("Gamma"), `# Gamma\n\n${b.body("Gamma")} ${b.link("GammaChild")} ${b.link("Shared")}\n`);
    for (const title of ["AlphaChild", "BetaChild", "GammaChild", "Shared"]) await write(b.path(title), `# ${title}\n\n${b.body(title)}\n`);
    const made = await key.sh(`mkdir ${closed}`);
    expect(made.isError, made.text).toBe(false);
    await mark(closed);

    const added = await key.sh(`publish add "${b.path("Start")}" --depth 2`);
    expect(added.isError, redact(added.text)).toBe(false);
    const link = linkOf(added.text);
    const count = async () => /notes: (\d+)$/.exec((await key.sh(`publish ls /Work/${folder}`)).text)?.[1];
    expect(sorted(await readCopy(link))).toEqual(["Alpha", "AlphaChild", "Beta", "BetaChild", "Gamma", "GammaChild", "Shared", "Start"]);
    expect(await count()).toBe("8");

    // A mark set later: Alpha and AlphaChild leave; Shared stays, Gamma still reaches it.
    await mark(b.path("Alpha"));
    let copy = await readCopy(link);
    expect(sorted(copy)).toEqual(["Beta", "BetaChild", "Gamma", "GammaChild", "Shared", "Start"]);
    expect(copy.notes![0].markdown, "the link to Alpha is its text now").toMatch(
      new RegExp(`^${b.name("Alpha")}, \\[${b.name("Beta")}\\]\\(#kn-\\d\\), \\[${b.name("Gamma")}\\]\\(#kn-\\d\\)\n$`),
    );
    holdsNothingOf(copy, [b.body("Alpha"), b.body("AlphaChild"), folder]);
    expect(await count()).toBe("6");

    // Moved into a marked folder: Beta and BetaChild leave.
    const moved = await key.sh(`mv "${b.path("Beta")}" ${closed}/`);
    expect(moved.isError, moved.text).toBe(false);
    copy = await readCopy(link);
    expect(sorted(copy)).toEqual(["Gamma", "GammaChild", "Shared", "Start"]);
    holdsNothingOf(copy, [b.body("Alpha"), b.body("AlphaChild"), b.body("Beta"), b.body("BetaChild"), folder]);
    expect(await count()).toBe("4");

    // Removed: Gamma leaves, and with it GammaChild and Shared, which nothing in the copy reaches any more.
    const removed = await key.sh(`rm "${b.path("Gamma")}"`);
    expect(removed.isError, removed.text).toBe(false);
    copy = await readCopy(link);
    expect(titles(copy)).toEqual(["Start"]);
    expect(copy.markdown).toBe(`${b.name("Alpha")}, ${b.name("Beta")}, ${b.name("Gamma")}\n`);
    holdsNothingOf(copy, [...["Alpha", "AlphaChild", "Beta", "BetaChild", "Gamma", "GammaChild", "Shared"].map((t) => b.body(t)), folder]);
    // The bundle stays, with the start note alone.
    expect((await key.sh(`publish ls /Work/${folder}`)).text).toMatch(new RegExp(`^${escape(b.path("Start"))} {2}.+ UTC {2}older: no$`));
  });

  test("publish: a key without May publish is refused, and the depth goes from 0 to 5 @publish", async ({ folder, mcp }) => {
    const b = bundle(folder);
    afterTest(() => cleanUp(b.path("Start"), [], [b.path("Start")]));
    await write(b.path("Start"), "# Start\n");
    // The worker's key has Work: write, no "May publish".
    expect(await mcp.sh(`publish add "${b.path("Start")}" --dry-run`)).toEqual({
      text: `publish add: this key may not publish in Work; its owner can allow it ("May publish") under Access → API keys or Apps in the web app`,
      isError: true,
    });
    expect(await key.sh(`publish add "${b.path("Start")}" --depth 6 --dry-run`)).toEqual({
      text: "publish add: --depth takes a number from 0 to 5",
      isError: true,
    });
    expect((await key.sh(`publish add "${b.path("Start")}" --depth 5 --dry-run`)).text).toBe(`would publish ${b.path("Start")}; nothing was published`);
    expect((await key.sh(`publish ls /Work/${folder}`)).text).toBe(`no published notes below /Work/${folder}`);
  });
});
