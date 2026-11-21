// SPDX-License-Identifier: AGPL-3.0-or-later
// Project memory (docs/superpowers/specs/2026-11-15-project-memory-design.md; PRs #223, #250), through MCP.
//
// Promises in scope, and the test that covers each:
// 1. Start-up skills in the handshake (§2): `project-memory` under the head "Skills for the start of every session"
//    → "the handshake names project-memory under the start-up head".
// 2. A start-up skill in her own vault is listed under the same head, after the system skills (§2, SkillInstructions.cs)
//    → "a start-up skill Alice writes in her own vault …".
// 3. The mark counts only in her own vaults with no other user's write: Bob's marked skill in the shared vault is an
//    ordinary skill, for Alice and for Bob (§2, SkillDirectory.cs) → "a start-up skill Bob writes in the shared vault …".
// 4. Hint only names the start-up skills, never a shared one, and no task skills (§2) → "hint only names …".
// 5. Only her own vault Skills replaces a system skill; a `_skills/project-memory` (Bob's, in the shared vault) doesn't, in the list or in
//    `skills cat` (§1) → "a _skills/project-memory doesn't replace the system skill".
//    Left out: the replacement by `/Skills/project-memory` itself: Alice has no vault Skills, and creating one would
//    change her vault list while the browser projects run.
// 5a. A start-up skill a former member last changed stays another user's once the vault is no longer shared (PR #250)
//    → "a start-up skill a former member wrote stays another user's".
// 6. A missing skills section (left out silently past 5 s, HandshakeInstructions.cs) fails every handshake test above:
//    each one asserts the section's heads, so it can't pass by omission.
// 7. `cat` with several paths (§3): each under '==> path <==' with a version token of its own, a failing path in place,
//    a note that doesn't fit prints its outline, `--version` takes one path → "cat prints several notes …".
// 8. `rm` and `mv` with several paths (§4): one line per path, a failing one reported and the others done, several
//    sources (notes, an attachment, a folder) into a folder, a link between two notes moved together pointing at the moved note, no -r
//    → "rm and mv take several paths …".
// 9. `rg -g` with ripgrep's rules (§5): `*.md` at any depth, `!**/Archive/**`, `{a,b}`, a glob with `/` anchored to the
//    path given, also with `--files` → "rg -g filters paths …".
// 10. A message with `write`, `edit`, `replace` (§6): shown by `history`, a multi-note `replace` giving it to every
//     note it changes; over 200 characters or not one line refused
//     → "a message with write, edit and replace …".
// 11. Several replacements in one `edit` (§7): in order, all or none; the single pair still works
//     → "edit applies a list of replacements …". That they make one version can't be seen from outside: one key's
//     writes join one version anyway (NoteWriter.cs:106-108).
// Left out: the web app's history showing the message (browser, not this project); the three-client session test
// (§Testing: a model in the loop); which message a coalesced version keeps: the spec leaves keeping the last or joining
// them to the builder. (Within one key's changeset the writes coalesce deterministically, so the message test reads
// history after each write.)
import { randomBytes } from "node:crypto";
import { Api, Mcp } from "../support/api";
import { secrets } from "../support/env";
import { reservePassword } from "../support/session";
import { shareWithBob, unshare, type Share } from "../support/share";
import { expect, test } from "./fixtures";

const startHead =
  "Skills for the start of every session: read each with sh `skills cat <name>` and follow it before your first answer, even to a greeting, alongside any start-up steps of your client or the project:";
const listHead =
  "Skills: instructions the user keeps for tasks they do again (sh: help skills). Before a task, look for a skill whose description matches it and follow it: read it with sh `skills cat <name>`, its files with `skills cat <name>/<file>`. Skills you may use:";
const hint =
  "Skills: the user keeps skills, instructions for tasks they do again, in the vault Skills, in a vault's _skills/ folder, and built in (sh: help skills). Before a task, run sh `skills find \"<the user's request>\"` and follow a skill whose description matches; read it with `skills cat <name>`. New skills, sharing, moving between vaults and updates are the user's to do, in Kumbu²'s Skills screen.";
const memoryLine = /^- project-memory \(system\): Read and keep the person's memory/;

/** The `- ` lines under a head, up to the first line that isn't one; undefined when the head isn't there. */
function entries(instructions: string, head: string): string[] | undefined {
  const at = instructions.indexOf(head);
  if (at < 0) return undefined;
  const lines = instructions.slice(at + head.length).split("\n").slice(1);
  const end = lines.findIndex((l) => !l.startsWith("- "));
  return end < 0 ? lines : lines.slice(0, end);
}

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const versionOf = (text: string, path: string) => text.match(new RegExp(`^version of ${escape(path)}: (\\w+)$`, "m"))?.[1];
const manifest = (name: string, description: string) =>
  `---\nname: ${name}\ndescription: ${description}\nmetadata:\n  kumbu-when: session-start\n---\n# ${name}\n\nSay hello.\n`;

/** The version lines of `history <path>`, newest first. */
async function historyLines(mcp: Mcp, path: string) {
  const { text, isError } = await mcp.sh(`history ${path}`);
  expect(isError, text).toBe(false);
  return text.split("\n").filter((l) => /^[0-9a-f]{12} {2}/.test(l));
}

test.describe("memory", () => {
  let share: Share;
  let alice: Api;
  let bob: Api;
  /** Alice's agent with the shared vault too, and Bob's agent, writing in it. */
  let aliceKey: Mcp;
  let bobKey: Mcp;

  test.beforeAll(async () => {
    // Keys, invitations and deletions wait on the 5-a-minute password ledger, shared with other workers.
    test.setTimeout(4 * 60_000);
    share = await shareWithBob("Team", "write");
    alice = await Api.as("alice");
    bob = await Api.as("bob");
    aliceKey = await Mcp.create(alice, `e2e memory alice ${randomBytes(3).toString("hex")}`, { Work: "write", [share.vault]: "read" });
    bobKey = await Mcp.create(bob, `e2e memory bob ${randomBytes(3).toString("hex")}`, { [share.vault]: "write" });
  });

  test.afterAll(async () => {
    // Keys, invitations and deletions wait on the 5-a-minute password ledger, shared with other workers.
    test.setTimeout(4 * 60_000);
    await aliceKey?.dispose();
    await bobKey?.dispose();
    await alice?.dispose();
    await bob?.dispose();
    if (share) await unshare(share);
  });

  test("memory: the handshake names project-memory under the start-up head @memory", async ({ mcp }) => {
    const { instructions = "" } = await mcp.initialize();
    // Left out silently past 5 s (HandshakeInstructions.cs): a missing section fails here.
    const start = entries(instructions, startHead);
    expect(start, "the start-up head is in the instructions").toBeDefined();
    expect(start![0]).toMatch(memoryLine);
    // Not listed again with the task skills.
    const list = entries(instructions, listHead);
    expect(list, "the list head follows").toBeDefined();
    expect(list!.some((l) => l.startsWith("- project-memory "))).toBe(false);
    expect(list).toContainEqual(expect.stringMatching(/^- create-skill \(system\): /));
  });

  test("memory: a start-up skill Alice writes in her own vault is listed under the head, after the system skills @memory", async ({ folder }) => {
    const name = folder;
    const path = `/Work/_skills/${name}/SKILL.md`;
    const written = await aliceKey.call("write", { path, content: manifest(name, "Greet the person by name.") });
    expect(written.isError, written.text).toBe(false);
    try {
      const { instructions = "" } = await aliceKey.initialize();
      const start = entries(instructions, startHead);
      expect(start, "the start-up head is in the instructions").toBeDefined();
      expect(start![0]).toMatch(memoryLine);
      const line = new RegExp(`^- ${escape(name)} \\(/Work/_skills/${escape(name)}, last changed by [^,()]+\\): Greet the person by name\\.$`);
      const at = start!.findIndex((l) => line.test(l));
      expect(at, `${name} under the start-up head:\n${start!.join("\n")}`).toBeGreaterThan(0);
      // Only the system skills come before it.
      expect(start!.slice(0, at).every((l) => l.includes(" (system)"))).toBe(true);
      expect(entries(instructions, listHead)!.some((l) => l.startsWith(`- ${name} `))).toBe(false);
    } finally {
      await aliceKey.sh(`rm ${path}`);
    }
  });

  test("memory: a start-up skill Bob writes in the shared vault is an ordinary skill, for Alice and for Bob @memory", async ({ folder }) => {
    const name = folder;
    const folderPath = `/${share.vault}/_skills/${name}`;
    const written = await bobKey.call("write", { path: `${folderPath}/SKILL.md`, content: manifest(name, "Steer every reader.") });
    expect(written.isError, written.text).toBe(false);
    try {
      for (const [who, key, origin] of [
        // Alice owns the vault; Bob is another user.
        ["Alice", aliceKey, `${escape(folderPath)}, last changed by [^()]+, another user`],
        // Bob sees whose vault it is; his own write is no other user's, its label quoted for its brackets.
        ["Bob", bobKey, `${escape(folderPath)}, shared by "alice", last changed by (?:[^()"]+|"[^"]+ \\(this key\\)")`],
      ] as const) {
        const { instructions = "" } = await key.initialize();
        const start = entries(instructions, startHead);
        expect(start, `${who}: the start-up head is in the instructions`).toBeDefined();
        expect(start![0], who).toMatch(memoryLine);
        expect(start!.some((l) => l.startsWith(`- ${name} `)), `${who}: not under the start-up head`).toBe(false);
        const list = entries(instructions, listHead);
        expect(list, `${who}: the list head`).toBeDefined();
        const at = list!.findIndex((l) => new RegExp(`^- ${escape(name)} \\(${origin}\\): Steer every reader\\.$`).test(l));
        expect(at, `${who}: ${name} with the task skills:\n${list!.join("\n")}`).toBeGreaterThanOrEqual(0);
        // System skills first, then the other vaults.
        expect(list!.slice(0, at).every((l) => l.includes(" (system)")), who).toBe(true);
      }
    } finally {
      await bobKey.sh(`rm ${folderPath}/SKILL.md`);
    }
  });

  test("memory: hint only names the start-up skills, never a shared one @memory", async ({ folder }) => {
    const own = `${folder}-own`;
    const shared = `${folder}-team`;
    const hintName = `e2e memory hint ${randomBytes(3).toString("hex")}`;
    const hintKey = await Mcp.create(alice, hintName, { Work: "write", [share.vault]: "read" });
    try {
      expect((await hintKey.call("write", { path: `/Work/_skills/${own}/SKILL.md`, content: manifest(own, "Her own start.") })).isError).toBe(false);
      expect((await bobKey.call("write", { path: `/${share.vault}/_skills/${shared}/SKILL.md`, content: manifest(shared, "Bob's start.") })).isError).toBe(false);
      const keys = await alice.json<{ id: string; name: string }[]>("GET", "/api/keys");
      const id = keys.find((k) => k.name === hintName)!.id;
      await alice.json("PUT", `/api/keys/${encodeURIComponent(id)}/instructions`, { mode: "hint" });
      const listed = await alice.json<{ id: string; instructions: string }[]>("GET", "/api/keys");
      expect(listed.find((k) => k.id === id)?.instructions).toBe("hint");

      const { instructions = "" } = await hintKey.initialize();
      const start = entries(instructions, startHead);
      expect(start, "the start-up head is in the instructions").toBeDefined();
      expect(start![0]).toMatch(memoryLine);
      expect(start).toContainEqual(expect.stringMatching(new RegExp(`^- ${escape(own)} \\(/Work/_skills/${escape(own)}, last changed by [^,()]+\\): Her own start\\.$`)));
      // The hint follows the start-up skills; no list, no task skill, nothing of Bob's.
      expect(instructions).toContain(`\n\n${hint}`);
      expect(instructions.endsWith(hint)).toBe(true);
      expect(instructions).not.toContain(listHead);
      expect(instructions).not.toContain("create-skill");
      expect(instructions).not.toContain(shared);
    } finally {
      await hintKey.sh(`rm /Work/_skills/${own}/SKILL.md`);
      await bobKey.sh(`rm /${share.vault}/_skills/${shared}/SKILL.md`);
      await hintKey.dispose();
    }
  });

  // Bob writes it in the shared vault: a member must not swap the skill every session follows for its readers.
  test("memory: a _skills/project-memory doesn't replace the system skill @memory", async () => {
    const path = `/${share.vault}/_skills/project-memory/SKILL.md`;
    const content = `---\nname: project-memory\ndescription: A stand-in from the e2e suite.\nmetadata:\n  kumbu-when: session-start\n---\nStand-in body ${randomBytes(3).toString("hex")}.\n`;
    const written = await bobKey.call("write", { path, content });
    expect(written.isError, written.text).toBe(false);
    try {
      const { instructions = "" } = await aliceKey.initialize();
      const start = entries(instructions, startHead);
      expect(start, "the start-up head is in the instructions").toBeDefined();
      expect(start![0]).toMatch(memoryLine);
      expect(start!.filter((l) => l.startsWith("- project-memory ")), "only the system skill at the start").toHaveLength(1);
      // The system skill is still there by its full name.
      const bySystem = await aliceKey.sh("skills cat system:project-memory");
      expect(bySystem.isError, bySystem.text).toBe(false);
      expect(bySystem.text).toContain("A memory is a folder with a `MEMORY.md`");
      // The head says to read it with `skills cat <name>`: the name reads the system skill, the vault's one its path.
      const cat = await aliceKey.sh("skills cat project-memory");
      expect(cat.isError, `M-01: skills cat project-memory reads the system skill, not: ${cat.text}`).toBe(false);
      expect(cat.text).toContain("A memory is a folder with a `MEMORY.md`");
      expect(cat.text).not.toContain("Stand-in body");
    } finally {
      await bobKey.sh(`rm ${path}`);
    }
  });
});

test("memory: a start-up skill a former member wrote stays another user's @memory", async ({ folder }) => {
  // Its keys, shares and deletions wait on the 5-a-minute password ledger, shared with other workers.
  test.setTimeout(4 * 60_000);
  // A share of its own: Bob leaves it, which the describe's shared vault must not see.
  const former = await shareWithBob("Former", "write");
  const name = folder;
  const path = `/${former.vault}/_skills/${name}/SKILL.md`;
  const alice = await Api.as("alice");
  const bob = await Api.as("bob");
  let left = false;
  try {
    const bobKey = await Mcp.create(bob, `e2e memory former ${randomBytes(3).toString("hex")}`, { [former.vault]: "write" });
    try {
      const written = await bobKey.call("write", { path, content: manifest(name, "Left behind.") });
      expect(written.isError, written.text).toBe(false);
    } finally {
      await bobKey.dispose();
    }
    await bob.json("POST", `/api/memberships/${encodeURIComponent(former.membershipId)}/leave`);
    left = true;

    const aliceKey = await Mcp.create(alice, `e2e memory former ${randomBytes(3).toString("hex")}`, { [former.vault]: "read" });
    try {
      const { instructions = "" } = await aliceKey.initialize();
      const start = entries(instructions, startHead);
      expect(start, "the start-up head is in the instructions").toBeDefined();
      expect(start!.some((l) => l.startsWith(`- ${name} `)), "not under the start-up head").toBe(false);
      const list = entries(instructions, listHead);
      expect(list, "the list head").toBeDefined();
      expect(list).toContainEqual(
        expect.stringMatching(new RegExp(`^- ${escape(name)} \\(/${escape(former.vault)}/_skills/${escape(name)}, last changed by .+, another user\\): Left behind\\.$`)),
      );
    } finally {
      await aliceKey.dispose();
    }
  } finally {
    await alice.dispose();
    await bob.dispose();
    if (left) {
      const owner = await Api.as("alice");
      try {
        await reservePassword("alice");
        await owner.json("POST", `/api/vaults/${encodeURIComponent(former.vault)}/delete`, { password: secrets().passwords.alice, confirm: former.vault });
      } finally {
        await owner.dispose();
      }
    } else {
      await unshare(former);
    }
  }
});

test("memory: a message with write, edit and replace shows in history; a long or broken one is refused @memory", async ({ mcp, folder }) => {
  const dir = `/Work/${folder}`;
  const note = `${dir}/Why.md`;
  const also = `${dir}/Also.md`;
  const other = `${dir}/Never.md`;
  try {
    // Writes close together may join one version (NoteWriter.cs:71); the version shows the last message brought.
    const current = async () => (await historyLines(mcp, note))[0];
    const written = await mcp.call("write", { path: note, content: "alpha\n", message: "first: why it was made" });
    expect(written.isError, written.text).toBe(false);
    const v1 = versionOf(written.text, note)!;
    expect(await current()).toMatch(new RegExp(`^${v1} .* — first: why it was made {2}\\(current\\)$`));

    const edited = await mcp.call("edit", { path: note, version: v1, old_text: "alpha", new_text: "beta", message: "second: why it changed" });
    expect(edited.isError, edited.text).toBe(false);
    const v2 = versionOf(edited.text, note)!;
    expect(await current()).toMatch(new RegExp(`^${v2} .* — second: why it changed {2}\\(current\\)$`));

    // A second note for the multi-note replace.
    expect((await mcp.call("write", { path: also, content: "beta too\n" })).isError).toBe(false);
    const preview = await mcp.call("replace", { path: dir, old_text: "beta", new_text: "gamma" });
    const plan = preview.text.match(/^plan: (\w+)$/m)?.[1];
    expect(plan, preview.text).toBeTruthy();
    const replaced = await mcp.call("replace", { path: dir, old_text: "beta", new_text: "gamma", plan, message: "third: why it was replaced" });
    expect(replaced.isError, replaced.text).toBe(false);
    const v3 = versionOf((await mcp.sh(`cat ${note}`)).text, note)!;
    expect(await current()).toMatch(new RegExp(`^${v3} .* — third: why it was replaced {2}\\(current\\)$`));
    // Every note it changes gets the message.
    expect((await historyLines(mcp, also))[0]).toMatch(/ — third: why it was replaced {2}\(current\)$/);
    expect((await mcp.sh(`cat ${also}`)).text.startsWith("gamma too\n")).toBe(true);

    // Exactly 200 characters is one line still.
    const at200 = "x".repeat(200);
    const fourth = await mcp.call("write", { path: note, content: "delta\n", version: v3, message: at200 });
    expect(fourth.isError, fourth.text).toBe(false);
    const v4 = versionOf(fourth.text, note)!;
    expect(await current()).toMatch(new RegExp(`^${v4} .* — ${at200} {2}\\(current\\)$`));

    for (const message of ["y".repeat(201), "one line\nand another", "one line\u2028and another"]) {
      const shown = JSON.stringify(message.slice(0, 20));
      const write = await mcp.call("write", { path: other, content: "never\n", message });
      expect(write, shown).toEqual({ isError: true, text: "write: message is one line of at most 200 characters" });
      const edit = await mcp.call("edit", { path: note, version: v4, old_text: "delta", new_text: "epsilon", message });
      expect(edit, shown).toEqual({ isError: true, text: "edit: message is one line of at most 200 characters" });
      const replacing = await mcp.call("replace", { path: dir, old_text: "delta", new_text: "epsilon", message });
      expect(replacing, shown).toEqual({ isError: true, text: "replace: message is one line of at most 200 characters" });
    }
    // Nothing was written: no note, the same version.
    expect((await mcp.sh(`cat ${other}`)).isError).toBe(true);
    const after = await mcp.sh(`cat ${note}`);
    expect(versionOf(after.text, note)).toBe(v4);
    expect(after.text.startsWith("delta\n")).toBe(true);
  } finally {
    await mcp.sh(`rm ${note} ${also}`);
  }
});

test("memory: edit applies a list of replacements in order as one version, or none of them @memory", async ({ mcp, folder }) => {
  const note = `/Work/${folder}/List.md`;
  try {
    const written = await mcp.call("write", { path: note, content: "alpha beta gamma\n" });
    const v1 = versionOf(written.text, note)!;
    // The second pair matches only once the first is applied.
    const edited = await mcp.call("edit", {
      path: note,
      version: v1,
      edits: [
        { old_text: "alpha", new_text: "one" },
        { old_text: "one beta", new_text: "two" },
      ],
    });
    expect(edited.isError, edited.text).toBe(false);
    expect(edited.text).toContain(`edited ${note}: 2 changes`);
    const v2 = versionOf(edited.text, note)!;
    let read = await mcp.sh(`cat ${note}`);
    expect(read.text.startsWith("two gamma\n")).toBe(true);
    expect(versionOf(read.text, note)).toBe(v2);

    // One pair that doesn't match: nothing changes.
    const refused = await mcp.call("edit", {
      path: note,
      version: v2,
      edits: [
        { old_text: "gamma", new_text: "delta" },
        { old_text: "missing", new_text: "x" },
      ],
    });
    expect(refused).toEqual({
      isError: true,
      text: `edit: edit 2 of 2: old_text not found in ${note}; it must match exactly, including whitespace and line breaks (cat it to see the current text)`,
    });
    read = await mcp.sh(`cat ${note}`);
    expect(read.text.startsWith("two gamma\n")).toBe(true);
    expect(versionOf(read.text, note)).toBe(v2);

    // Today's single pair still works.
    const single = await mcp.call("edit", { path: note, version: v2, old_text: "gamma", new_text: "delta" });
    expect(single.isError, single.text).toBe(false);
    expect((await mcp.sh(`cat ${note}`)).text.startsWith("two delta\n")).toBe(true);
  } finally {
    await mcp.sh(`rm ${note}`);
  }
});

test("memory: cat prints several notes, each under its header with its own version token @memory", async ({ mcp, folder }) => {
  const dir = `/Work/${folder}`;
  const [a, b, missing, big1, big2] = ["A.md", "B.md", "Missing.md", "Big1.md", "Big2.md"].map((n) => `${dir}/${n}`);
  // Two notes of about 15,000 characters: together over the call's limit (OutputLimit.cs, 24,000).
  const big = (title: string) => `# ${title}\n\n## First part\n\n${"word ".repeat(1500)}\n\n## Second part\n\n${"word ".repeat(1500)}\n`;
  try {
    const va = versionOf((await mcp.call("write", { path: a, content: "note a\n" })).text, a)!;
    const vb = versionOf((await mcp.call("write", { path: b, content: "note b\n" })).text, b)!;
    const read = await mcp.sh(`cat ${a} ${missing} ${b}`);
    // Each under its header, the failing path's error in its place, the others go on.
    const body = read.text.split("\n\nversion of ")[0];
    expect(body).toMatch(new RegExp(`^==> ${escape(a)} <==\nnote a\n\ncat: ${escape(missing)}: No such file or directory\n\n==> ${escape(b)} <==\nnote b\n?$`));
    expect(versionOf(read.text, a)).toBe(va);
    expect(versionOf(read.text, b)).toBe(vb);
    expect(versionOf(read.text, missing)).toBeUndefined();

    // --version stays single-path.
    expect(await mcp.sh(`cat --version ${va} ${a} ${b}`)).toEqual({ isError: true, text: "cat: --version takes one path" });

    // A note that doesn't fit prints its outline, marked, never cut; it keeps its version token.
    const v1 = versionOf((await mcp.call("write", { path: big1, content: big("Big one") })).text, big1)!;
    const v2 = versionOf((await mcp.call("write", { path: big2, content: big("Big two") })).text, big2)!;
    const both = await mcp.sh(`cat ${big1} ${big2}`);
    expect(both.isError).toBe(false);
    expect(both.text.startsWith(`==> ${big1} <==\n${big("Big one")}`)).toBe(true);
    const second = both.text.slice(both.text.indexOf(`==> ${big2} <==`)).split("\n\nversion of ")[0];
    expect(second.startsWith(`==> ${big2} <==\n[outline only: too long for this call; read it alone or by section]\n`)).toBe(true);
    expect(second).toContain("First part");
    expect(second).toContain("Second part");
    expect(second).not.toContain("word word");
    expect(versionOf(both.text, big1)).toBe(v1);
    expect(versionOf(both.text, big2)).toBe(v2);
  } finally {
    await mcp.sh(`rm ${a} ${b} ${big1} ${big2}`);
  }
});

test("memory: rm and mv take several paths, each on its own @memory", async ({ mcp, aliceApi, folder }) => {
  const dir = `/Work/${folder}`;
  try {
    // A and C link to each other through relative paths that break when both move.
    expect((await mcp.call("write", { path: `${dir}/A.md`, content: "see [c](Other/C.md)\n" })).isError).toBe(false);
    expect((await mcp.call("write", { path: `${dir}/Other/C.md`, content: "back to [a](../A.md)\n" })).isError).toBe(false);
    expect((await mcp.call("write", { path: `${dir}/B.md`, content: "b\n" })).isError).toBe(false);
    // Sources may mix notes, attachments and folders.
    await aliceApi.putFile(`${dir}/plain.txt`, Buffer.from("plain\n"));
    expect((await mcp.call("write", { path: `${dir}/Folder/F.md`, content: "f\n" })).isError).toBe(false);
    expect((await mcp.sh(`mkdir ${dir}/Sub`)).isError).toBe(false);

    // More than one source needs a folder target.
    expect(await mcp.sh(`mv ${dir}/A.md ${dir}/B.md ${dir}/D.md`)).toEqual({
      isError: true,
      text: "mv: with several sources the target must be a folder, ending in '/'",
    });

    const moved = await mcp.sh(`mv ${dir}/A.md ${dir}/Other/C.md ${dir}/B.md ${dir}/plain.txt ${dir}/Folder/ ${dir}/Sub/`);
    expect(moved.isError, moved.text).toBe(false);
    const lines = moved.text.trim().split("\n");
    expect(lines).toHaveLength(5);
    expect(lines[3]).toMatch(new RegExp(`^moved ${escape(dir)}/plain\\.txt to ${escape(dir)}/Sub/plain\\.txt\\b`));
    expect(lines[4]).toMatch(new RegExp(`^moved ${escape(dir)}/Folder/? to ${escape(dir)}/Sub/Folder/?[; ]`));
    expect(lines[0]).toMatch(new RegExp(`^moved ${escape(dir)}/A\\.md to ${escape(dir)}/Sub/A\\.md; `));
    expect(lines[1]).toMatch(new RegExp(`^moved ${escape(dir)}/Other/C\\.md to ${escape(dir)}/Sub/C\\.md; `));
    expect(lines[2]).toBe(`moved ${dir}/B.md to ${dir}/Sub/B.md; no links needed rewriting`);
    // Moved in order, so each link points at the moved note.
    const read = await mcp.sh(`cat ${dir}/Sub/A.md ${dir}/Sub/C.md`);
    expect(read.text).toContain(`see [c](/${folder}/Sub/C.md)\n`);
    expect(read.text).toContain(`back to [a](/${folder}/Sub/A.md)\n`);
    expect((await mcp.sh(`ls ${dir}`)).text.trim().split("\n")).toEqual(["Sub/"]);
    expect((await mcp.sh(`ls ${dir}/Sub`)).text.trim().split("\n").sort()).toEqual(["A.md", "B.md", "C.md", "Folder/", "plain.txt  text 6 B"]);
    expect((await mcp.sh(`ls ${dir}/Sub/Folder`)).text.trim().split("\n")).toEqual(["F.md"]);

    // A folder with notes needs no -r, and has none.
    const folderRm = await mcp.sh(`rm ${dir}/Sub`);
    expect(folderRm.isError).toBe(true);
    expect(folderRm.text).toMatch(new RegExp(`^rm: ${escape(dir)}/Sub: Is a directory holding \\d+ entr(y|ies) \\(rm removes notes, files and empty folders; there is no -r\\)$`));

    // One that fails is reported in its place; the others go ahead.
    const removed = await mcp.sh(`rm ${dir}/Sub/A.md ${dir}/Sub/Nope.md ${dir}/Sub/B.md`);
    expect(removed.isError).toBe(true);
    const rmLines = removed.text.trim().split("\n");
    expect(rmLines).toHaveLength(3);
    expect(rmLines[0]).toMatch(new RegExp(`^removed ${escape(dir)}/Sub/A\\.md; it stays in the trash for 30 days`));
    expect(rmLines[1]).toBe(`rm: ${dir}/Sub/Nope.md: No such file or directory`);
    expect(rmLines[2]).toMatch(new RegExp(`^removed ${escape(dir)}/Sub/B\\.md; it stays in the trash for 30 days`));
    expect((await mcp.sh(`ls ${dir}/Sub`)).text.trim().split("\n").sort()).toEqual(["C.md", "Folder/", "plain.txt  text 6 B"]);
  } finally {
    for (const at of [dir, `${dir}/Sub`]) {
      await mcp.sh(`rm ${["A.md", "B.md", "Other/C.md", "C.md", "plain.txt", "Folder/F.md"].map((p) => `${at}/${p}`).join(" ")}`);
    }
    await mcp.sh(`rm ${dir}/Sub/Folder ${dir}/Folder ${dir}/Sub ${dir}/Other`);
  }
});

test("memory: rg -g filters paths with ripgrep's rules @memory", async ({ mcp, aliceApi, folder }) => {
  const dir = `/Work/${folder}`;
  const notes = ["A.md", "B.md", "C.md", "Archive/Old.md", "Sub/Deep.md"];
  const all = [...notes, "plain.txt"];
  const word = `needle${randomBytes(3).toString("hex")}`;
  /** The paths below the folder that a call names, relative to it, sorted. */
  const found = async (command: string) => {
    const { text, isError } = await mcp.sh(command);
    expect(isError, text).toBe(false);
    return [...new Set(text.trim().split("\n").filter(Boolean).map((l) => l.split(":")[0].slice(dir.length + 1)))].sort();
  };
  try {
    for (const name of notes) expect((await mcp.call("write", { path: `${dir}/${name}`, content: `${word}\n` })).isError).toBe(false);
    await aliceApi.putFile(`${dir}/plain.txt`, Buffer.from(`${word}\n`));
    // Without a glob, rg finds the word everywhere, the text file and the archive included.
    expect(await found(`rg ${word} ${dir}`)).toEqual([...all].sort());
    // Without '/', a glob matches the file name at any depth.
    expect(await found(`rg ${word} ${dir} -g '*.md'`)).toEqual([...notes].sort());
    expect(await found(`rg ${word} ${dir} -g '!**/Archive/**'`)).toEqual(all.filter((p) => !p.startsWith("Archive/")).sort());
    expect(await found(`rg ${word} ${dir} -g '{A,B}.md'`)).toEqual(["A.md", "B.md"]);
    // With '/', it is anchored to the path given; '*' doesn't cross '/'.
    expect(await found(`rg ${word} ${dir} -g 'Sub/*.md'`)).toEqual(["Sub/Deep.md"]);
    expect(await found(`rg ${word} ${dir} -g '*/*.md'`)).toEqual(["Archive/Old.md", "Sub/Deep.md"]);
    // Repeatable, and with --files too.
    expect(await found(`rg --files ${dir} -g '*.md' -g '!**/Archive/**' -g '!C.md'`)).toEqual(["A.md", "B.md", "Sub/Deep.md"]);
  } finally {
    await mcp.sh(`rm ${all.map((p) => `${dir}/${p}`).join(" ")}`);
  }
});
