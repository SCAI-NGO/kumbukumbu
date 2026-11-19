// SPDX-License-Identifier: AGPL-3.0-or-later
// Who wrote what (docs/superpowers/specs/2026-11-18-blame-design.md; PR #253), through MCP and the console. Two
// writers: Alice in the web app (`Api.putNote`, her session) and her agent (the `mcp` key). The agent's name is the
// one `history` shows for its write, so blame is checked against the labels the history shows (decision 7).
//
// Promises in scope, and the test that covers each:
//  1. Each line's writer is the last to change it; unchanged lines keep theirs, changed and added ones get the
//     writer of the write (§4); `blame` prints runs with their newest time, then the counts without blank lines, and
//     the note's version as `cat` does (§6) → "blame by run and by line …"
//  2. `--lines` prints every line with its writer, time and text (§6) → "blame by run and by line …"
//  3. `blame <note>#Section` limits it to that section (§6) → "blame by run and by line …"
//  4. `blame <folder>`: per note, lines per writer, then `total: …` per writer and the number of notes (§6)
//     → "blame of a folder …"
//  5. mv's link rewrite gives the rewritten lines to the mover as "(mv)", in a note linking to the moved one and in
//     the moved note itself; a plain move keeps the record (§5.4 table, §12) → "a renamed note's rewritten links …"
//  6. `replace` gives the changed lines to "label (replace)", a restore of a version to "label (restore)", only
//     those lines (§5.4) → "replace and restore give only the lines they change …"
//  7. Whoever reads the note's history reads its blame, with the labels the history shows: to Bob (a read-only
//     member of Work) the owner is "alice" quoted and her agent "alice"'s agent, never the key's name (§8,
//     anti-goal) → "Bob, a member, sees blame as he sees history …"
//  8. Refusals as `history`: an attachment ("only notes have lines", and /api/blame 400), a missing path, a hidden
//     discreet vault ("No such file or directory", /api/blame 404) (§6, §7.2, §8) → "blame refuses what history refuses …"
//  9. Its help as §6 prints it, the usage line and then "Who wrote what: …"; its summary in `help` and its line in
//     the sh tool's description (§6) → "blame refuses …"
// 10. A run's time is its newest line's, `--lines` each line's own, as `history` times the writes (§6, §13)
//     → "blame by run and by line …"
// 11. `edit` (MCP) gives the changed line to the caller; `rm`, then `restore` from the trash, changes no line's
//     writer: the record comes back with the note, though history's current row is the restorer's (§5.4)
//     → "an edit takes its line; rm and restore from the trash keep the record …"
// 12. The output limit (PR #253): runs and lines are fitted to it, with the hint to blame a section in place of
//     the rest, and the counts line always kept → "a note too long for one call …"
// Left out: notes from before the record ("earlier than", the sentence): not reachable on a fresh stack, every note is
// written by 0.6.0; a dropped record (4,096 changes of writer) needs thousands of writes; the 200-note cap of a folder
// needs 201 notes; `cp` is not a command of the agent's shell ("cat the note, then create the copy with the write
// tool"), so a copy is a plain create; a member without history rights doesn't exist (decision 7: whoever reads the
// note reads its history); two members shown alike ("another user") needs a second member account; the calendar,
// board, archive and checkpoint paths are covered by the xUnit tests the spec names (§10); a WebDAV PUT ("label
// (dav)"): the stack runs on plain HTTP and /dav answers 403 "WebDAV needs HTTPS"; an Obsidian device's write needs
// the plugin's sync client and a device key, which the suite has no client for (xUnit covers both, §10); Duplicate in
// the web app: e2e/blame/blame.spec.ts "blame #4"; a damaged note's refusal: no call from outside can damage a sealed
// note.
import type { Api } from "../support/api";
import { expect, test } from "./fixtures";

const time = /^\d{4}-\d\d-\d\d \d\d:\d\d UTC$/;

/** An answer's rows before its version block, each split into its padded columns. */
const rows = (text: string) =>
  text
    .split("\n\nversion of ")[0]
    .split("\n")
    .filter((l) => l.length > 0)
    .map((l) => l.split(/ {2,}/));

/** `[range, writer]` of each run, the time checked to be a time. */
const runs = (text: string) =>
  rows(text).slice(0, -1).map(([range, writer, at]) => {
    expect(at, `the time of ${range}`).toMatch(time);
    return [range, writer];
  });

/** The counts line below the runs. */
const counts = (text: string) => rows(text).at(-1)!.join("  ");

/** The writer `history` names for the note's current version: the label blame must show. */
async function currentWriter(run: (command: string) => Promise<{ text: string }>, path: string) {
  const first = (await run(`history "${path}"`)).text.split("\n")[0];
  expect(first, "history's first line is the current version").toMatch(/\(current\)$/);
  return first.split(/ {2,}/)[2];
}

/** `history`'s time of each writer's version: the newest write of each, as blame must time its lines. */
async function historyTimes(run: (command: string) => Promise<{ text: string }>, path: string) {
  const versions = (await run(`history "${path}"`)).text.split("\n\nversion of ")[0].split("\n").map((l) => l.split(/ {2,}/));
  const times = new Map<string, string>();
  // Newest first: the first row of each writer is her newest write.
  for (const [, at, writer] of versions) if (!times.has(writer)) times.set(writer, at);
  return times;
}

const write = async (mcp: { call: (t: string, a: Record<string, unknown>) => Promise<{ text: string; isError: boolean }> }, path: string, version: string, content: string) => {
  const written = await mcp.call("write", { path, version, content });
  expect(written.isError, written.text).toBe(false);
  return written.text.match(/^version of .*: (\w+)$/m)![1];
};

/** Alice's note in the web app, then her agent's write over it: line 4 changed, a section added. */
async function protocol(api: Api, mcp: Parameters<typeof write>[0], path: string) {
  const created = await api.putNote(path, "# Protocol\n\nstep one\nstep two\n\n## Results\n\nfine\n");
  const version = await write(mcp, path, created.version, "# Protocol\n\nstep one\nstep two, checked\n\n## Results\n\nfine\n\n## Agent\n\nfound it\n");
  return version;
}

type Shell = { sh: (command: string) => Promise<{ text: string; isError: boolean }> };

/** The notes and files below a folder, from `ls` (a folder ends in `/`). */
async function below(mcp: Shell, dir: string): Promise<string[]> {
  const listed = await mcp.sh(`ls "${dir}"`);
  if (listed.isError) return [];
  // A file's row goes on with its kind and size after two spaces.
  const names = listed.text.split("\n").filter((l) => l.length > 0).map((l) => l.split(/ {2,}/)[0]);
  return (await Promise.all(names.map((n) => (n.endsWith("/") ? below(mcp, `${dir}/${n.slice(0, -1)}`) : [`${dir}/${n}`])))).flat();
}

/** Everything below the folder to the trash (there is no `rm -r`); its folders have no marker, so they go with it. */
async function clear(mcp: Shell, folder: string) {
  const paths = await below(mcp, `/Work/${folder}`);
  if (paths.length === 0) return;
  const removed = await mcp.sh(`rm ${paths.map((p) => `"${p}"`).join(" ")}`);
  expect(removed.isError, removed.text).toBe(false);
}

test.afterEach(async ({ mcp, folder }) => {
  await clear(mcp, folder);
});

test("blame by run and by line: each line's last writer, the counts and the version @blame", async ({ mcp, folder, aliceApi }) => {
  const path = `/Work/${folder}/Protocol.md`;
  const version = await protocol(aliceApi, mcp, path);
  const agent = await currentWriter((c) => mcp.sh(c), path);
  expect(agent, "the agent's key, by its name").toMatch(/^e2e mcp worker \d+ [0-9a-f]{6}$/);

  const blamed = await mcp.sh(`blame "${path}"`);
  expect(blamed.isError, blamed.text).toBe(false);
  expect(runs(blamed.text)).toEqual([
    ["1-3", "alice"],
    ["4", agent],
    ["5-8", "alice"],
    ["9-12", agent],
  ]);
  expect(counts(blamed.text)).toBe(`alice 4 lines, ${agent} 3 (blank lines not counted)`);
  // Each run's time is its writer's write, as history times it.
  const times = await historyTimes((c) => mcp.sh(c), path);
  expect([...times.keys()]).toEqual([agent, "alice"]);
  expect(rows(blamed.text).slice(0, -1).map(([range, , at]) => [range, at])).toEqual(
    rows(blamed.text).slice(0, -1).map(([range, writer]) => [range, times.get(writer)]),
  );
  // The version as `cat` carries it, for an edit on it.
  expect(blamed.text).toContain(`\n\nversion of ${path}: ${version}\n`);

  // The same from Alice's session, the console: the same labels.
  const console = await aliceApi.console(`blame "${path}"`);
  expect(console.isError, console.text).toBe(false);
  expect(runs(console.text)).toEqual(runs(blamed.text));

  const byLine = await mcp.sh(`blame --lines "${path}"`);
  expect(byLine.isError, byLine.text).toBe(false);
  const lines = byLine.text.split("\n\nversion of ")[0].split("\n");
  const text = ["# Protocol", "", "step one", "step two, checked", "", "## Results", "", "fine", "", "## Agent", "", "found it"];
  expect(lines).toHaveLength(text.length);
  lines.forEach((line, i) => {
    const writer = i === 3 || i >= 8 ? agent : "alice";
    const match = line.match(/^(\d+) +(.+?) +(\d{4}-\d\d-\d\d \d\d:\d\d UTC)  (.*)$/);
    expect(match, `line ${i + 1}: ${line}`).not.toBeNull();
    expect([match![1], match![2], match![3], match![4]]).toEqual([`${i + 1}`, writer, times.get(writer), text[i]]);
  });

  // A section: its own lines only, their numbers as in the note.
  const section = await mcp.sh(`blame "${path}#Agent"`);
  expect(section.isError, section.text).toBe(false);
  expect(runs(section.text)).toEqual([["10-12", agent]]);
  expect(counts(section.text)).toBe(`${agent} 2 lines (blank lines not counted)`);
  // From its heading to the line before the next one; whether the blank line 9 is in it the spec doesn't say.
  const results = await mcp.sh(`blame --lines "${path}#Results"`);
  const numbers = results.text.split("\n\nversion of ")[0].split("\n").map((l) => Number(l.split(/ {2,}/)[0]));
  expect(numbers[0]).toBe(6);
  expect(numbers).toContain(8);
  expect(Math.max(...numbers)).toBeLessThan(10);
});

test("blame of a folder: per note, lines per writer, then the total @blame", async ({ mcp, folder, aliceApi }) => {
  await protocol(aliceApi, mcp, `/Work/${folder}/Protocol.md`);
  await aliceApi.putNote(`/Work/${folder}/Sub/Hers.md`, "# Hers\n\nmine\n");
  const theirs = `/Work/${folder}/Sub/Theirs.md`;
  const written = await mcp.call("write", { path: theirs, content: "# Theirs\n\n\n" });
  expect(written.isError, written.text).toBe(false);
  const agent = await currentWriter((c) => mcp.sh(c), theirs);

  const blamed = await mcp.sh(`blame "/Work/${folder}"`);
  expect(blamed.isError, blamed.text).toBe(false);
  expect(rows(blamed.text)).toEqual([
    [`/Work/${folder}/Protocol.md`, `alice 4, ${agent} 3`],
    [`/Work/${folder}/Sub/Hers.md`, "alice 2"],
    [`/Work/${folder}/Sub/Theirs.md`, `${agent} 1`],
    [`total: alice 6 lines in 2 notes, ${agent} 4 in 2`],
  ]);
  // A folder below: only its notes.
  const sub = await mcp.sh(`blame "/Work/${folder}/Sub/"`);
  expect(rows(sub.text).at(-1)).toEqual([`total: alice 2 lines in 1 note, ${agent} 1 in 1`]);
});

test("a renamed note's rewritten links go to the renamer as (mv), in a linking note and in the moved note itself @blame", async ({ mcp, folder, aliceApi }) => {
  // Names of the test's own: wiki links resolve vault-wide.
  const target = `Target ${folder}`;
  const linking = `/Work/${folder}/Linking.md`;
  await aliceApi.putNote(`/Work/${folder}/${target}.md`, "# Target\n");
  await aliceApi.putNote(linking, `# Linking\n\nsee [[${target}]]\nplain\n`);

  const moved = await mcp.sh(`mv "/Work/${folder}/${target}.md" "/Work/${folder}/${target} 2.md"`);
  expect(moved.isError, moved.text).toBe(false);
  expect(moved.text).toContain(`rewrote 1 link in ${linking}`);
  const agent = await currentWriter((c) => mcp.sh(c), linking);
  expect(agent, "history names the move's writer as blame does").toMatch(/^e2e mcp worker \d+ [0-9a-f]{6} \(mv\)$/);

  const blamed = await mcp.sh(`blame --lines "${linking}"`);
  expect(blamed.text.split("\n\nversion of ")[0].split("\n").map((l) => l.split(/ {2,}/).slice(0, 2))).toEqual([
    ["1", "alice"],
    ["2", "alice"],
    ["3", agent],
    ["4", "alice"],
  ]);
  // A plain move changes no line's writer.
  const target2 = await mcp.sh(`blame "/Work/${folder}/${target} 2.md"`);
  expect(runs(target2.text)).toEqual([["1", "alice"]]);

  // The moved note's own relative link, rewritten as it goes into a subfolder: that line is the mover's, the rest stays.
  // A relative link that a name alone wouldn't settle: a second Other.md in the folder makes the move rewrite it.
  const mover = `/Work/${folder}/Mover.md`;
  await aliceApi.putNote(`/Work/${folder}/Other.md`, "# Other\n");
  await aliceApi.putNote(`/Work/${folder}/Elsewhere/Other.md`, "# Other, elsewhere\n");
  await aliceApi.putNote(mover, "# Mover\n\nsee [other](Other.md)\nplain\n");
  const into = await mcp.sh(`mv "${mover}" "/Work/${folder}/Sub/Mover.md"`);
  expect(into.isError, into.text).toBe(false);
  const own = await mcp.sh(`blame --lines "/Work/${folder}/Sub/Mover.md"`);
  const ownLines = own.text.split("\n\nversion of ")[0].split("\n").map((l) => l.split(/ {2,}/));
  expect(ownLines[2][3], "the link now leads from the subfolder").toBe(`see [other](/${folder}/Other.md)`);
  expect(ownLines.map((l) => l.slice(0, 2))).toEqual([
    ["1", "alice"],
    ["2", "alice"],
    ["3", agent],
    ["4", "alice"],
  ]);
});

test("an edit takes its line; rm and restore from the trash keep the record @blame", async ({ mcp, folder, aliceApi }) => {
  const path = `/Work/${folder}/Notes.md`;
  const created = await aliceApi.putNote(path, "# Notes\n\nfirst\nsecond\n");
  const edited = await mcp.call("edit", { path, version: created.version, old_text: "second", new_text: "second, edited" });
  expect(edited.isError, edited.text).toBe(false);
  const agent = await currentWriter((c) => mcp.sh(c), path);
  const before = await mcp.sh(`blame --lines "${path}"`);
  expect(rows(before.text).map((r) => r.slice(0, 2))).toEqual([
    ["1", "alice"],
    ["2", "alice"],
    ["3", "alice"],
    ["4", agent],
  ]);

  // Alice removes it in the web app and her agent brings it back: the record went into the trash with it.
  await aliceApi.deleteNote(path);
  const restored = await mcp.sh(`restore "${path}"`);
  expect(restored.isError, restored.text).toBe(false);
  expect(restored.text).toContain(`restored ${path} from the trash`);
  const history = (await mcp.sh(`history "${path}"`)).text.split("\n")[0];
  expect(history, "history's current row is the restore").toMatch(/ {2}\(current\) {2}\(restored\)$/);
  const after = await mcp.sh(`blame --lines "${path}"`);
  expect(rows(after.text)).toEqual(rows(before.text));
});

test("a note too long for one call: runs and lines fitted to the output limit, the counts kept @blame", async ({ mcp, folder, aliceApi }) => {
  // 600 lines, every other one rewritten by the agent: 600 runs, more than the shell's 24,000 characters hold.
  const path = `/Work/${folder}/Long.md`;
  const lines = Array.from({ length: 600 }, (_, i) => `line ${i + 1}`);
  const created = await aliceApi.putNote(path, `${lines.join("\n")}\n`);
  await write(mcp, path, created.version, `${lines.map((l, i) => (i % 2 === 0 ? l.toUpperCase() : l)).join("\n")}\n`);
  const agent = await currentWriter((c) => mcp.sh(c), path);
  const outlineHint = `('outline ${path}' lists them)]`;
  const limit = 24_000;

  const blamed = await mcp.sh(`blame "${path}"`);
  expect(blamed.isError, blamed.text).toBe(false);
  const body = blamed.text.split("\n\nversion of ")[0];
  expect(body.length).toBeLessThanOrEqual(limit);
  const shown = body.split("\n");
  expect(shown.slice(0, 2).map((l) => l.split(/ {2,}/).slice(0, 2))).toEqual([
    ["1", agent],
    ["2", "alice"],
  ]);
  expect(shown.at(-2)).toBe(`[too many runs for one call; blame a section: blame "${path}#Section" ${outlineHint}`);
  expect(shown.at(-1), "the counts survive the limit").toBe(`${agent} 300 lines, alice 300 (blank lines not counted)`);

  const byLine = await mcp.sh(`blame --lines "${path}"`);
  expect(byLine.isError, byLine.text).toBe(false);
  const lineBody = byLine.text.split("\n\nversion of ")[0];
  expect(lineBody.length).toBeLessThanOrEqual(limit);
  const lineRows = lineBody.split("\n");
  expect(lineRows[0]).toMatch(new RegExp(`^1 +${agent} +\\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC  LINE 1$`));
  expect(lineRows.at(-1)).toBe(`[too long for one call; blame a section: blame --lines "${path}#Section" ${outlineHint}`);
});

test("replace and restore give only the lines they change to (replace) and (restore) @blame", async ({ mcp, folder, aliceApi }) => {
  const path = `/Work/${folder}/Log.md`;
  const first = await aliceApi.putNote(path, "# Log\n\nalpha\nbeta\n");
  const args = { path: `/Work/${folder}`, old_text: "alpha", new_text: "ALPHA" };
  const preview = await mcp.call("replace", args);
  const plan = preview.text.match(/^plan: (\w+)$/m)?.[1];
  expect(plan, preview.text).toBeDefined();
  const applied = await mcp.call("replace", { ...args, plan });
  expect(applied.isError, applied.text).toBe(false);
  const replacer = await currentWriter((c) => mcp.sh(c), path);
  expect(replacer).toMatch(/^e2e mcp worker \d+ [0-9a-f]{6} \(replace\)$/);
  expect(runs((await mcp.sh(`blame "${path}"`)).text)).toEqual([
    ["1-2", "alice"],
    ["3", replacer],
    ["4", "alice"],
  ]);

  // Back to Alice's first version: line 3 differs from the current state, so it goes to the restorer.
  const restored = await mcp.sh(`restore "${path}" ${first.version}`);
  expect(restored.isError, restored.text).toBe(false);
  const restorer = await currentWriter((c) => mcp.sh(c), path);
  expect(restorer).toMatch(/^e2e mcp worker \d+ [0-9a-f]{6} \(restore\)$/);
  expect(runs((await mcp.sh(`blame "${path}"`)).text)).toEqual([
    ["1-2", "alice"],
    ["3", restorer],
    ["4", "alice"],
  ]);
});

test("Bob, a member, sees blame as he sees history: the owner and her agent, never the key's name @blame", async ({ mcp, folder, aliceApi, bobApi }) => {
  const path = `/Work/${folder}/Protocol.md`;
  await protocol(aliceApi, mcp, path);
  const key = await currentWriter((c) => mcp.sh(c), path);

  const agent = await currentWriter((c) => bobApi.console(c), path);
  expect(agent).toBe(`"alice"'s agent`);
  const blamed = await bobApi.console(`blame "${path}"`);
  expect(blamed.isError, blamed.text).toBe(false);
  expect(runs(blamed.text)).toEqual([
    ["1-3", `"alice"`],
    ["4", agent],
    ["5-8", `"alice"`],
    ["9-12", agent],
  ]);
  expect(counts(blamed.text)).toBe(`"alice" 4 lines, ${agent} 3 (blank lines not counted)`);
  expect(blamed.text, "the key's name stays the owner's").not.toContain(key);

  const view = await bobApi.json<{ writers: { display: string }[] }>("GET", `/api/blame/Work/${folder}/Protocol.md`);
  expect(view.writers.map((w) => w.display)).toEqual([`"alice"`, agent]);
});

test("blame refuses what history refuses: an attachment, a missing note, a hidden vault @blame", async ({ mcp, folder, aliceApi }) => {
  const image = `/Work/${folder}/pixel.png`;
  await aliceApi.putFile(image, Buffer.from("89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d4944415478da63f8cfc0f01f0005000201ffa65a6a0000000049454e44ae426082", "hex"));
  const refused = await mcp.sh(`blame "${image}"`);
  expect(refused).toEqual({ isError: true, text: `blame: ${image}: only notes have lines` });
  const api = await aliceApi.send("GET", `/api/blame/Work/${folder}/pixel.png`);
  expect(api.status()).toBe(400);
  expect(((await api.json()) as { error: string }).error).toMatch(/is an attachment: only notes have lines\.$/);

  const missing = `/Work/${folder}/Nowhere.md`;
  expect(await mcp.sh(`blame "${missing}"`)).toEqual({ isError: true, text: `blame: ${missing}: No such file or directory` });
  expect((await aliceApi.send("GET", `/api/blame/Work/${folder}/Nowhere.md`)).status()).toBe(404);

  // Home is discreet and hidden in Alice's saved session: blame answers as history does.
  const home = await aliceApi.console("blame /Home/Zahnarzt.md");
  expect(home).toEqual({ isError: true, text: "blame: /Home/Zahnarzt.md: No such file or directory" });
  expect((await aliceApi.console("history /Home/Zahnarzt.md")).text).toBe("history: /Home/Zahnarzt.md: No such file or directory");
  expect((await aliceApi.send("GET", "/api/blame/Home/Zahnarzt.md")).status()).toBe(404);

  // Its help and the sh tool's description.
  expect((await mcp.sh("help blame")).text.startsWith("blame [--lines] <path>\n  Who wrote what:"), "help blame starts as §6 prints it").toBe(true);
  expect((await mcp.sh("help")).text).toMatch(/^ {2}blame \[--lines\] <path>\[#section\] +who wrote what: the writer of each run of lines of a note; of a folder: lines per writer$/m);
  const { message } = await mcp.raw("tools/list", {});
  const sh = (message?.result as { tools: { name: string; description: string }[] }).tools.find((t) => t.name === "sh");
  expect(sh?.description).toContain("blame [--lines] <path>          who wrote what: the last writer of each run of lines of a note");
});
