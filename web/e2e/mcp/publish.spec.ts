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
// 8. A link and a note embed both count as an edge; an embed of a note in the copy points at it, one of a note left out
//    is its name; links and embeds to a heading or block land there (`#kn-N/Heading`, `#kn-N/%5Eid`); images go along,
//    a PDF stays behind, said in the answer (decisions 1, 2, 5) → "publish: embeds are edges …".
// 9. Link for your AI carries the whole bundle, each note under its own heading, links as anchors (decision 6) → the
//    same test, through the reader's cookie and POST /api/s/ai-links (the token redacted before any assertion).
// 10. `publish update` prints what changed in the set (added:, removed:); the activity records "published … with N
//    notes" and "updated …: +1, −1 notes" (decisions 4, 10) → "publish: update prints what changed in the set …".
// 11. Marking or removing the start note withdraws the whole publication (decision 3) → "publish: marking or removing
//    the start note …".
// 12. Over HTTP: past depth 0 `expected` is required (400), a stale `expected` publishes nothing (409 with the walk as
//    it is), `without` names only notes of the walk, never the start note (400), the depth goes from 0 to 5 (400)
//    ("Left to the builder"; API) → "publish: over HTTP a bundle needs the list …".
// 13. A member never publishes (decision 8); the sh tool's description and `help publish` tell the agent to show the
//    list first ("The flows") → "publish: a member never publishes …".
// 14. Who prunes: a member's removal of a bundled note takes it and what lies only behind it out of the copy; the start
//    note stays the owner's to remove ("Left to the builder") → "publish: a member's removal prunes the copy …", in a
//    vault Alice shares with Bob at write.
// Left out: the limits of 500 notes and 64 MB (decision 9): not cheap to reach on the stack, covered by xUnit; the bar
// above the notes (decision 7), Update copy's list and the dialog's 409 (decision 4, 2): the web app, covered in
// e2e/publishing/publish.spec.ts; `publish add` itself never answers 409, as `sh` walks and publishes in one step.
import { request, type APIRequestContext } from "@playwright/test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { randomBytes } from "node:crypto";
import { Api, encodePath, guarded, Mcp } from "../support/api";
import { baseURL, fixturesDir } from "../support/env";
import { reserveReaderRead } from "../support/session";
import { shareWithBob, unshare, type Share } from "../support/share";
import { expect, redact, test } from "./fixtures";

type ReaderNote = {
  id: number;
  title: string;
  markdown: string;
  depth: number;
  files: number[];
};
type ReaderView = { title: string; markdown: string; copy: string; files: { index: number; mediaType: string }[]; notes?: ReaderNote[] };

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

/** The copy as a reader opens it, and the reader's context, which holds its cookie (for the AI link); dispose it. */
async function openCopy(link: string): Promise<{ view: ReaderView; anon: APIRequestContext }> {
  const anon = await request.newContext({ baseURL, extraHTTPHeaders: { Origin: baseURL } });
  await reserveReaderRead();
  const response = await guarded(() => anon.post("/api/s", { data: { code: new URL(link).hash.slice(1) } }), "POST /api/s");
  expect(response.status(), "the reader's read").toBe(200);
  return { view: (await response.json()) as ReaderView, anon };
}

/** The status of a reader's read of the link. */
async function readStatus(link: string) {
  const anon = await request.newContext({ baseURL, extraHTTPHeaders: { Origin: baseURL } });
  try {
    await reserveReaderRead();
    return (await guarded(() => anon.post("/api/s", { data: { code: new URL(link).hash.slice(1) } }), "POST /api/s")).status();
  } finally {
    await anon.dispose();
  }
}

/** The copy's titles in walk order; a single note's is the copy's own. */
const titles = (view: ReaderView) => (view.notes ?? [{ title: view.title }]).map((n) => n.title);
const sorted = (view: ReaderView) => [...titles(view)].sort();
/** The id of the copy's note with this title. */
const idOf = (view: ReaderView, title: string) => view.notes!.find((n) => n.title === title)!.id;

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
    expect(/^link /m.test(dry.text), redact(dry.text)).toBe(false);
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
    // The link to Alpha is its text now; those to Beta and Gamma point at their new ids, as do Gamma's.
    expect(copy.notes![0].markdown).toBe(
      `${b.name("Alpha")}, [${b.name("Beta")}](#kn-${idOf(copy, "Beta")}), [${b.name("Gamma")}](#kn-${idOf(copy, "Gamma")})\n`,
    );
    expect(copy.notes!.find((n) => n.title === "Gamma")!.markdown).toBe(
      `${b.body("Gamma")} [${b.name("GammaChild")}](#kn-${idOf(copy, "GammaChild")}) [${b.name("Shared")}](#kn-${idOf(copy, "Shared")})\n`,
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
    expect(redact((await key.sh(`publish add "${b.path("Start")}" --depth 5 --dry-run`)).text)).toBe(`would publish ${b.path("Start")}; nothing was published`);
    expect((await key.sh(`publish ls /Work/${folder}`)).text).toBe(`no published notes below /Work/${folder}`);
  });
  test("publish: embeds are edges, links and embeds to headings and blocks stay in the copy, images go along and a PDF doesn't @publish", async ({
    folder,
  }) => {
    const b = bundle(folder);
    const image = `/Work/${folder}/one-${folder.slice(-6)}.png`;
    const pdf = `/Work/${folder}/manual-${folder.slice(-6)}.pdf`;
    afterTest(() => cleanUp(b.path("Start"), [b.path("Out")], [...["Start", "Embedded", "Sections", "Out"].map((t) => b.path(t)), image, pdf]));
    await alice.putFile(image, readFileSync(join(fixturesDir, "tags", "ailink", "Pump", "one.png")));
    await alice.putFile(pdf, readFileSync(join(fixturesDir, "tags", "recent", "Pump", "manual.pdf")));
    const [png, manual] = [image, pdf].map((p) => p.split("/").pop()!);
    // Embedded is reached by its embed alone; Out is marked.
    await write(
      b.path("Start"),
      `# Start\n\n![[${b.name("Embedded")}]]\n\n![[${b.name("Sections")}#Part]]\n\n[[${b.name("Sections")}#^b1|the block]] and [[${b.name("Sections")}#Part|the part]]\n\n![[${b.name("Out")}]]\n\n![[${png}]]\n\n![[${manual}]]\n`,
    );
    await write(b.path("Embedded"), `# Embedded\n\n${b.body("Embedded")}\n`);
    await write(b.path("Sections"), `# Sections\n\n## Part\n\nPart text.\n\n## Other\n\nOther text. ^b1\n`);
    await write(b.path("Out"), `# Out\n\n${b.body("Out")}\n`);
    await mark(b.path("Out"));

    // The agent sees the PDF stay behind before anything goes out.
    const dry = await key.sh(`publish add "${b.path("Start")}" --depth 1 --dry-run`);
    expect(redact(dry.text).split("\n").at(-1)).toBe(`not copied, plain text instead: ${manual}; PDFs are not published yet`);
    const added = await key.sh(`publish add "${b.path("Start")}" --depth 1`);
    expect(added.isError, redact(added.text)).toBe(false);
    expect(listed(added.text)).toEqual({ notes: ["Start", "Embedded", "Sections"].map((t) => b.path(t)), leftOut: { [b.path("Out")]: reasons.unpublishable } });
    // The PDF stays behind, said in the answer.
    expect(redact(added.text).split("\n").at(-1)).toBe(`not copied, plain text instead: ${manual}; PDFs are not published yet`);

    const { view, anon } = await openCopy(linkOf(added.text));
    try {
      expect(titles(view)).toEqual(["Start", "Embedded", "Sections"]);
      // An embed of a note in the copy points at it (the reader shows it in place), one to a heading or block at that
      // part; an embed of a note left out is its name; the image is the copy's file 0; the PDF is its name.
      expect(view.notes![0].markdown).toBe(
        `![${b.name("Embedded")}](#kn-1)\n\n![${b.name("Sections")}](#kn-2/Part)\n\n[the block](#kn-2/%5Eb1) and [the part](#kn-2/Part)\n\n${b.name("Out")}\n\n![${png}](f/0)\n\n${manual}\n`,
      );
      expect(view.files).toEqual([{ index: 0, mediaType: "image/png" }]);
      expect(view.notes!.map((n) => n.files)).toEqual([[0], [], []]);
      holdsNothingOf(view, [b.body("Out"), folder]);

      // Link for your AI carries the whole bundle: each note under its own heading, links between them as anchors.
      await reserveReaderRead();
      const made = await guarded(() => anon.post("/api/s/ai-links", { headers: { "X-Kumbu-Copy": view.copy } }), "POST /api/s/ai-links");
      expect(made.status(), "an AI link is made").toBe(200);
      const ai = ((await made.json()) as { link: string }).link;
      await reserveReaderRead();
      const answer = await guarded(() => anon.get(ai), "GET the AI link");
      expect(answer.status(), "the AI link answers").toBe(200);
      // Its image line holds the token: redacted before any assertion.
      const body = redact(await answer.text());
      expect(body.split("\n").slice(0, 3)).toEqual([
        "# Start",
        "",
        expect.stringMatching(
          /^A published note with 2 linked notes, snapshot of \d{4}-\d\d-\d\d \d\d:\d\d UTC; links between them point to the headings below, links to other notes are plain text\.$/,
        ),
      ]);
      expect(body).toContain(`\n![${b.name("Embedded")}](#n1)\n`);
      expect(body).toContain(`\n[the block](#n2) and [the part](#n2)\n`);
      expect(body).toContain(`\n---\n\n# Embedded {#n1}\n\n${b.body("Embedded")}\n`);
      expect(body).toContain(`\n---\n\n# Sections {#n2}\n\n## Part\n`);
      expect(body.includes(b.body("Out")), "the left-out note's text in the AI link's body").toBe(false);
    } finally {
      await anon.dispose();
    }
  });

  test("publish: update prints what changed in the set, and the activity records publishing and updating @publish", async ({ folder }) => {
    const b = bundle(folder);
    afterTest(() =>
      cleanUp(
        b.path("Start"),
        [],
        ["Start", "Alpha", "Beta", "Gamma"].map((t) => b.path(t)),
      ),
    );
    await write(b.path("Start"), `# Start\n\n${b.link("Alpha")} ${b.link("Beta")}\n`);
    for (const title of ["Alpha", "Beta", "Gamma"]) await write(b.path(title), `# ${title}\n`);
    const added = await key.sh(`publish add "${b.path("Start")}" --depth 1`);
    expect(added.isError, redact(added.text)).toBe(false);

    // Beta's link goes, Gamma's comes.
    await alice.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")} ${b.link("Gamma")}\n`);
    const updated = await key.sh(`publish update "${b.path("Start")}"`);
    expect(updated.isError, redact(updated.text)).toBe(false);
    const lines = redact(updated.text).split("\n");
    expect(lines.slice(lines.indexOf("added:"))).toEqual(["added:", `  ${b.path("Gamma")}`, "removed:", `  ${b.path("Beta")}`]);
    expect(listed(updated.text).notes).toEqual(["Start", "Alpha", "Gamma"].map((t) => b.path(t)));

    const activity = await alice.json<{ events: { kind: string; text: string }[] }>("GET", "/api/vaults/Work/activity?limit=50");
    const mine = activity.events.map((e) => e.text).filter((t) => t.includes(b.name("Start")));
    expect(mine).toEqual(
      expect.arrayContaining([
        expect.stringMatching(new RegExp(` published ${escape(b.path("Start"))} with 3 notes\\.$`)),
        expect.stringMatching(new RegExp(` updated the published copy of ${escape(b.path("Start"))}: \\+1, \u22121 notes\\.$`)),
      ]),
    );
  });

  test("publish: marking or removing the start note withdraws the whole publication @publish", async ({ folder }) => {
    const b = bundle(folder);
    afterTest(() => cleanUp(b.path("Start"), [b.path("Start")], [b.path("Start"), b.path("Alpha")]));
    await write(b.path("Start"), `# Start\n\n${b.link("Alpha")}\n`);
    await write(b.path("Alpha"), "# Alpha\n");

    // Marked: the link reads nothing, and nothing is listed.
    const first = await key.sh(`publish add "${b.path("Start")}" --depth 1`);
    expect(first.isError, redact(first.text)).toBe(false);
    const link = linkOf(first.text);
    expect(titles(await readCopy(link))).toEqual(["Start", "Alpha"]);
    await mark(b.path("Start"));
    expect(await readStatus(link), "the marked start note's link").toBe(404);
    expect(redact((await key.sh(`publish ls /Work/${folder}`)).text)).toBe(`no published notes below /Work/${folder}`);
    expect((await alice.send("DELETE", `/api/unpublishable/${encodePath(b.path("Start"))}`)).status(), "the mark lifted").toBe(204);

    // Removed: the same.
    const second = await key.sh(`publish add "${b.path("Start")}" --depth 1`);
    expect(second.isError, redact(second.text)).toBe(false);
    const again = linkOf(second.text);
    expect(titles(await readCopy(again))).toEqual(["Start", "Alpha"]);
    const removed = await key.sh(`rm "${b.path("Start")}"`);
    expect(removed.isError, removed.text).toBe(false);
    expect(await readStatus(again), "the removed start note's link").toBe(404);
    expect(redact((await key.sh(`publish ls /Work/${folder}`)).text)).toBe(`no published notes below /Work/${folder}`);
  });

  test("publish: over HTTP a bundle needs the list it was checked against, and the walk's own notes @publish", async ({ folder }) => {
    const b = bundle(folder);
    afterTest(() =>
      cleanUp(
        b.path("Start"),
        [],
        ["Start", "Alpha", "Beta", "Elsewhere"].map((t) => b.path(t)),
      ),
    );
    await write(b.path("Start"), `# Start\n\n${b.link("Alpha")}\n`);
    await write(b.path("Alpha"), `# Alpha\n\n${b.link("Beta")}\n`);
    for (const title of ["Beta", "Elsewhere"]) await write(b.path(title), `# ${title}\n`);
    // Paths in the vault, as the API takes them.
    const inVault = (title: string) => Api.inVault(b.path(title));
    const url = `/api/publications/${encodePath(b.path("Start"))}`;
    const post = async (data: Record<string, unknown>) => {
      const response = await alice.send("POST", url, { data });
      // A 201 would hold the link: never printed.
      return { status: response.status(), body: response.status() === 201 ? "(published)" : await response.text() };
    };
    const error = (text: string) => JSON.stringify({ error: text });

    // Refused before the password is asked for.
    expect(await post({ depth: 1 })).toEqual({
      status: 400,
      body: error("Send expected, the notes the list showed: a note goes out with its linked notes only as they were seen."),
    });
    await alice.confirmPassword();
    expect(await post({ depth: 6, expected: [inVault("Start")] })).toEqual({ status: 400, body: error("The depth goes from 0 to 5.") });
    for (const title of ["Start", "Elsewhere", "Beta"]) {
      expect(await post({ depth: 1, without: [inVault(title)], expected: [inVault("Start")] }), `without ${title}`).toEqual({
        status: 400,
        body: error(`${b.path(title)} is not among the notes ${b.path("Start")} takes at depth 1; leave out only notes the list shows.`),
      });
    }
    // A list that isn't the walk's: nothing goes out, and the answer is the walk as it is.
    const stale = await post({ depth: 1, expected: [inVault("Start")] });
    expect(stale.status).toBe(409);
    expect(JSON.parse(stale.body)).toEqual({
      message: "Something changed in the vault since the list was shown: check the list again.",
      preview: expect.objectContaining({
        notes: [expect.objectContaining({ path: b.path("Start") }), expect.objectContaining({ path: b.path("Alpha") })],
        leftOut: [],
      }),
    });
    expect((await alice.send("GET", url)).status(), "nothing was published").toBe(404);
    expect(redact((await key.sh(`publish ls /Work/${folder}`)).text)).toBe(`no published notes below /Work/${folder}`);
  });

  test("publish: a member never publishes, and the tool's description says to show the list first @publish", async ({ folder, bobApi }) => {
    const b = bundle(folder);
    afterTest(() => cleanUp(b.path("Start"), [], [b.path("Start")]));
    await write(b.path("Start"), "# Start\n");
    // Bob reads Work; the owner alone publishes (decision 8), refused before any password.
    const response = await bobApi.send("POST", `/api/publications/${encodePath(b.path("Start"))}`, { data: {} });
    expect({ status: response.status(), body: await response.text() }).toEqual({
      status: 403,
      body: JSON.stringify({ error: "Only the owner of Work can publish its notes.", code: "not-owner" }),
    });

    const { message } = await key.raw("tools/list", {});
    const sh = (message?.result as { tools: { name: string; description: string }[] }).tools.find((t) => t.name === "sh");
    expect(sh?.description).toContain(
      "publish ls|add|update|rm <note> share a note, with --depth N the notes it links to, as a read-only copy behind a secret link; show the person the list it prints before publishing more than one note",
    );
    const help = await key.sh("help publish");
    expect(help.text).toContain(
      "add and update print every note the copy takes and every note left behind. Before you\npublish more than the one note, show that list to the person and let them confirm it.",
    );
  });
});

test.describe("publish in a vault Bob writes in", () => {
  let share: Share;
  let alice: Api;
  let bob: Api;
  /** Alice's agent, who may publish in the shared vault, and Bob's, who writes in it. */
  let aliceKey: Mcp;
  let bobKey: Mcp;

  test.beforeAll(async () => {
    share = await shareWithBob("Team", "write");
    alice = await Api.as("alice");
    bob = await Api.as("bob");
    aliceKey = await Mcp.create(alice, `e2e publish alice ${randomBytes(3).toString("hex")}`, { [share.vault]: "write" }, [share.vault]);
    bobKey = await Mcp.create(bob, `e2e publish bob ${randomBytes(3).toString("hex")}`, { [share.vault]: "write" });
  });

  test.afterAll(async () => {
    await aliceKey?.dispose();
    await bobKey?.dispose();
    await alice?.dispose();
    await bob?.dispose();
    // Deleting the vault takes its notes and publications with it.
    if (share) await unshare(share);
  });

  test("publish: a member's removal prunes the copy, but the start note stays the owner's @publish", async ({ folder }) => {
    const path = (title: string) => `/${share.vault}/${folder}/${title} ${folder.slice(-6)}.md`;
    const link = (title: string) => `[[${title} ${folder.slice(-6)}]]`;
    for (const [title, content] of [
      ["Start", `# Start\n\n${link("Child")}\n`],
      ["Child", `# Child\n\n${link("Grandchild")}\n`],
      ["Grandchild", "# Grandchild\n"],
    ]) {
      const written = await aliceKey.call("write", { path: path(title), content });
      expect(written.isError, written.text).toBe(false);
    }
    const added = await aliceKey.sh(`publish add "${path("Start")}" --depth 2`);
    expect(added.isError, redact(added.text)).toBe(false);
    const published = linkOf(added.text);
    expect(titles(await readCopy(published))).toEqual(["Start", "Child", "Grandchild"]);

    // Bob removes Child: it leaves the copy, and Grandchild, reached only through it, with it.
    const removed = await bobKey.sh(`rm "${path("Child")}"`);
    expect(removed.isError, removed.text).toBe(false);
    expect(titles(await readCopy(published))).toEqual(["Start"]);

    // The start note is the owner's to remove.
    const refused = await bobKey.sh(`rm "${path("Start")}"`);
    expect(refused.isError, refused.text).toBe(true);
    expect(refused.text).toContain(`${path("Start")} is published by the vault's owner; only the owner can remove it or move it into an unpublishable folder`);
    expect(titles(await readCopy(published))).toEqual(["Start"]);
    await aliceKey.sh(`publish rm "${path("Start")}"`);
  });
});
