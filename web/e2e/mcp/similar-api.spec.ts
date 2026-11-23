// SPDX-License-Identifier: AGPL-3.0-or-later
// Link prediction without a page (docs/superpowers/specs/2026-11-22-link-prediction-design.md, #306): the answers of
// `GET /api/similar`, `POST …/similar/link` and `POST …/similar/not-related`, and `sh similar --unlinked` for agents.
// The browser's side is web/e2e/similar/similar.spec.ts.
//
// Each test in a vault of its own (`Similar-<random>`, plan review point 7): Add link and Not related change the notes,
// and a copy of a fixture in another test's vault would score 1.00 and stand in the way. Measured with arctic-m on the
// meaning fixtures: the three diving notes 0.53–0.63 with each other, every other pair below 0.33; so only the diving
// notes are clear cases, and only membership is asserted, never a score's value (beyond the floor itself).
//
// Promises in scope, and the test that covers each:
//  1. `ready`, up to 5, cosine ≥ 0.5, same vault, not the archive, `pending` 0 once indexed (§3.5, §4)
//     → "similar-api #1" (the diving pair; nothing for the cake; a copy in Work and one in the archive left out),
//       "similar-api #2" (five of seven, one per note)
//  2. Not the archive, unless the note is in it (§3.5) → "similar-api #3"
//  3. Not linked either way (§3.5) → "similar-api #4"
//  4. Only notes, no attachments (§3.5) → "similar-api #5" (a PDF on decompression, found by `semsearch`, not offered)
//  5. Add link appends `- [[Other]]` to `## Related` at the end, made once; the suggestion leaves the list; read back
//     through `GET /api/notes` (§3.4) → "similar-api #6"
//  6. An existing `## Related` (any case, level 2) takes the item at its end (§3.4) → "similar-api #7"
//  7. The path without `.md` when another note of the vault has the title (§3.4) → "similar-api #8"
//  8. Not related writes `not-related:` with the path in the vault; hidden on both notes; `sh similar --unlinked` leaves
//     the pair out for an agent; undo takes it out again (§3.6) → "similar-api #9"
//  9. Only where the caller may change the note: a read member gets the suggestions, the answers are refused (§3.3,
//     §4) → "similar-api #10"
// 10. The states: `unindexed` for a note not indexed yet (§3.7, §4) → "similar-api #11"; `off` → "similar-api #12" (no
//     `@search`: runs in the phase with search off, and fails on a stack with it on)
// Left out: `preparing` (the model is loaded before the tests run, plan review point 5; unreachable without a stack of
// its own); the rights checked before the other note's existence (the read member's refusal covers the rights).
import { randomBytes } from "node:crypto";
import { Api, Mcp, seed } from "../support/api";
import { secrets } from "../support/env";
import { firstIndex, nextIndex, similarNotes, waitSimilarReady } from "../support/search";
import { reservePassword } from "../support/session";
import { shareWithBob, unshare } from "../support/share";
import { fixtureTree } from "../support/test";
import { expect, redact, test as base } from "./fixtures";

/** Alice's vault of this test's own, with the meaning fixtures at its top. */
async function newVault(api: Api) {
  const vault = `Similar-${randomBytes(3).toString("hex")}`;
  await api.json("POST", "/api/vaults", { name: vault });
  return vault;
}

async function dropVault(api: Api, vault: string) {
  await reservePassword("alice");
  await api.json("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { password: secrets().passwords.alice, confirm: vault });
}

const seedMeaning = (api: Api, vault: string) => seed(api, fixtureTree("tags/meaning", `/${vault}`));

const at = (vault: string) => ({
  decompression: `/${vault}/Diving/Decompression.md`,
  tables: `/${vault}/Diving/Dive tables.md`,
  computer: `/${vault}/Diving/Dive computer.md`,
  cake: `/${vault}/Baking/Chocolate cake.md`,
});

const pathsOf = async (api: Api, path: string) => (await similarNotes(api, path)).notes.map((n) => n.path).sort();

const link = (api: Api, vault: string, path: string, target: string) =>
  api.send("POST", `/api/vaults/${encodeURIComponent(vault)}/similar/link`, { data: { path, target } });
const notRelated = (api: Api, vault: string, path: string, other: string, undo = false) =>
  api.send("POST", `/api/vaults/${encodeURIComponent(vault)}/similar/not-related`, { data: { path, other, undo } });

/** A one-page PDF with `lines` in Helvetica: text a PDF reader extracts, so search by meaning embeds it. */
function pdfOf(lines: string[]) {
  const stream = `BT /F1 11 Tf 14 TL 50 750 Td ${lines.map((l) => `(${l.replace(/[()\\]/g, "\\$&")}) '`).join(" ")} ET`;
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`,
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
  ];
  let body = "%PDF-1.4\n";
  const offsets: number[] = [];
  objects.forEach((o, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${o}\nendobj\n`;
  });
  const xref = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, "0")} 00000 n \n`).join("")}`;
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body, "latin1");
}

/** A vault of Alice's for this test alone; deleted after it (its password check counted). */
const test = base.extend<{ vault: string }>({
  vault: [
    async ({ aliceApi }, use) => {
      const vault = await newVault(aliceApi);
      try {
        await use(vault);
      } finally {
        await dropVault(aliceApi, vault);
      }
    },
    { timeout: 240_000 },
  ],
});

test("similar-api #1: a diving note gets the other two diving notes, the cake nothing above the floor; not another vault's, not the archive's @similar @search", async ({ aliceApi, vault, folder }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  // Copies of Dive tables score 1.00 with it: one in Work, one in this vault's archive.
  const tables = (await aliceApi.getNote(n.tables)).content;
  const elsewhere = `/Work/${folder}/Dive tables.md`;
  await aliceApi.putNote(elsewhere, tables);
  await aliceApi.putNote(`/${vault}/_archive/Old dive tables.md`, tables);
  try {
    await waitSimilarReady(aliceApi, n.decompression, firstIndex);

    const view = await similarNotes(aliceApi, n.decompression);
    expect(view.state).toBe("ready");
    expect(view.pending, "every note of the vault compared").toBe(0);
    expect(view.notes.map((s) => s.path).sort(), "the two other diving notes, nothing of Work or the archive").toEqual([n.computer, n.tables].sort());
    for (const s of view.notes) {
      expect(s.score, `${s.path} at or above the floor`).toBeGreaterThanOrEqual(0.5);
      // The line of its closest section, and that section's first line of prose, as the note has them.
      const lines = (await aliceApi.getNote(s.path)).content.split("\n");
      expect(s.line, `${s.path}: a line of the note`).toBeGreaterThanOrEqual(1);
      expect(s.line).toBeLessThanOrEqual(lines.length);
      expect(lines, `${s.path}: its text is a line of the note`).toContain(s.text);
      expect(lines.indexOf(s.text) + 1, `${s.path}: the text is at or below the section's line`).toBeGreaterThanOrEqual(s.line);
    }

    const cake = await similarNotes(aliceApi, n.cake);
    expect(cake.state).toBe("ready");
    expect(cake.notes, "nothing close enough to the cake").toEqual([]);
  } finally {
    await aliceApi.deleteNote(elsewhere);
  }
});

test("similar-api #2: up to five suggestions, one per note @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  // Seven more notes of two sections each, Decompression's own two paragraphs: every section near 1.00 with it.
  const paragraphs = (await aliceApi.getNote(n.decompression)).content.trim().split("\n\n").slice(1);
  const copies = Array.from({ length: 7 }, (_, i) => `/${vault}/Logs/Dive log ${i + 1}.md`);
  for (const [i, path] of copies.entries())
    await aliceApi.putNote(path, `# Dive log ${i + 1}\n\n## Pressure\n\n${paragraphs[0]}\n\n## Ascent\n\n${paragraphs[1]}\n`);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);

  const view = await similarNotes(aliceApi, n.decompression);
  expect(view.state).toBe("ready");
  const paths = view.notes.map((s) => s.path);
  expect(paths, "at most five").toHaveLength(5);
  expect(new Set(paths).size, `one row per note: ${paths.join(", ")}`).toBe(5);
  for (const path of paths) expect(copies, "a copy of Decompression's paragraphs, above the two diving notes").toContain(path);
});

test("similar-api #3: a note in the archive gets the archive's notes too @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  test.info().annotations.push({ type: "finding", description: "M-03: a note in the archive gets no suggestions from the archive (SimilarApi scopes to the vault's root without Archive)" });
  test.fail();
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  const tables = (await aliceApi.getNote(n.tables)).content;
  const decompression = (await aliceApi.getNote(n.decompression)).content;
  const source = `/${vault}/_archive/Old decompression.md`;
  const archived = `/${vault}/_archive/Old dive tables.md`;
  await aliceApi.putNote(source, decompression.replace("# Decompression stops", "# Old decompression"));
  await aliceApi.putNote(archived, tables.replace("# Dive tables", "# Old dive tables"));
  await waitSimilarReady(aliceApi, source, firstIndex);

  // From outside the archive, the archive is left out; from inside, it is in.
  expect(await pathsOf(aliceApi, n.decompression), "the vault's notes outside the archive only").toEqual([n.computer, n.tables].sort());
  expect(await pathsOf(aliceApi, source), "M-03: the archived copy of Dive tables is suggested for an archived note").toContain(archived);
});

test("similar-api #4: a note it links, or that links it, is not suggested @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  expect(await pathsOf(aliceApi, n.decompression), "before the links").toEqual([n.computer, n.tables].sort());

  // Decompression links Dive tables; Dive computer links Decompression.
  await aliceApi.putNote(n.decompression, `${(await aliceApi.getNote(n.decompression)).content}\nSee [[Dive tables]].\n`);
  await aliceApi.putNote(n.computer, `${(await aliceApi.getNote(n.computer)).content}\nSee [[Decompression]].\n`);
  await waitSimilarReady(aliceApi, n.decompression, nextIndex);

  expect(await pathsOf(aliceApi, n.decompression), "neither the note it links nor the note linking it").toEqual([]);
  expect(await pathsOf(aliceApi, n.tables), "Dive tables: not Decompression, which links it").toEqual([n.computer]);
  expect(await pathsOf(aliceApi, n.computer), "Dive computer: not Decompression, which it links").toEqual([n.tables]);
});

test("similar-api #5: a PDF close in meaning is not suggested, only notes @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  const pdf = `/${vault}/Diving/Ascent rules.pdf`;
  await aliceApi.putFile(
    pdf,
    pdfOf([
      "Decompression stops and the ascent rate.",
      "On a deep dive the body takes up nitrogen under pressure. Ascending too fast lets that nitrogen",
      "form bubbles in the tissues and the blood, which is decompression sickness. So the diver ascends",
      "slowly and makes decompression stops at set depths until enough nitrogen has left the tissues.",
    ]),
  );
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  // The PDF is embedded: search by meaning finds it.
  await expect
    .poll(async () => (await aliceApi.console(`semsearch --meaning -l nitrogen bubbles decompression stops "/${vault}"`)).text, {
      message: "the PDF is found by meaning",
      timeout: nextIndex,
      intervals: [1000],
    })
    .toContain(pdf);

  expect(await pathsOf(aliceApi, n.decompression), "the diving notes, not the PDF").toEqual([n.computer, n.tables].sort());
});

test("similar-api #6: Add link appends - [[Other]] to a Related section made once at the end; the suggestion goes @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);

  const first = await link(aliceApi, vault, n.decompression, n.tables);
  expect(first.status(), await first.text()).toBe(200);
  const written = (await first.json()) as { path: string; version: string };
  const note = await aliceApi.getNote(n.decompression);
  expect(written).toEqual({ path: n.decompression, version: note.version });
  expect(note.content).toBe(`${before.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n`);

  const second = await link(aliceApi, vault, n.decompression, n.computer);
  expect(second.status(), await second.text()).toBe(200);
  expect((await aliceApi.getNote(n.decompression)).content, "one Related section, both items").toBe(
    `${before.trimEnd()}\n\n## Related\n\n- [[Dive tables]]\n- [[Dive computer]]\n`,
  );

  // Linked now, so not suggested: from either side.
  await waitSimilarReady(aliceApi, n.decompression, nextIndex);
  expect(await pathsOf(aliceApi, n.decompression)).toEqual([]);
  expect(await pathsOf(aliceApi, n.tables), "Dive tables: not Decompression, which links it now").toEqual([n.computer]);
});

test("similar-api #7: an existing related section, any case, takes the item at its end @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  const body = (await aliceApi.getNote(n.decompression)).content.trimEnd();
  await aliceApi.putNote(n.decompression, `${body}\n\n## RELATED\n\n- [[Chocolate cake]]\n\n## Log\n\nTwo dives today.\n`);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);

  const response = await link(aliceApi, vault, n.decompression, n.tables);
  expect(response.status(), await response.text()).toBe(200);
  expect((await aliceApi.getNote(n.decompression)).content).toBe(
    `${body}\n\n## RELATED\n\n- [[Chocolate cake]]\n- [[Dive tables]]\n\n## Log\n\nTwo dives today.\n`,
  );
});

test("similar-api #8: Add link writes the path when another note of the vault has the title @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  test.info().annotations.push({
    type: "finding",
    description: "M-04: with a second note named Dive tables in the vault, Add link from the same folder writes [[Dive tables]], not the path (the resolver's same-folder preference)",
  });
  test.fail();
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  // Another note of the name, far in meaning.
  await aliceApi.putNote(`/${vault}/Furniture/Dive tables.md`, "# Dive tables\n\nThe oak tables of the harbour pub, sanded and oiled every spring.\n");
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);

  const response = await link(aliceApi, vault, n.decompression, n.tables);
  expect(response.status(), await response.text()).toBe(200);
  expect((await aliceApi.getNote(n.decompression)).content, "M-04: the link names the path, as the title is ambiguous in the vault").toBe(
    `${before.trimEnd()}\n\n## Related\n\n- [[Diving/Dive tables]]\n`,
  );
});

test("similar-api #9: Not related keeps the path in not-related, hides the pair on both sides and for agents; undo takes it back @similar @search", async ({ aliceApi, vault }) => {
  // The first index, two later ones, and the agent's key, whose password check may wait 61 s on the ledger.
  test.setTimeout(360_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  const before = (await aliceApi.getNote(n.decompression)).content;
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);
  const agent = await Mcp.create(aliceApi, `e2e similar ${randomBytes(3).toString("hex")}`, { [vault]: "read" });
  try {
    const unlinked = async (from: string) => {
      const { text, isError } = await agent.sh(`similar "${from}" "/${vault}" --unlinked`);
      expect(isError, redact(text)).toBe(false);
      return text;
    };
    expect(await unlinked(n.decompression), "before: the agent sees Dive tables").toContain(n.tables);

    const response = await notRelated(aliceApi, vault, n.decompression, n.tables);
    expect(response.status(), await response.text()).toBe(200);
    // Into the note the answer was on, the path in the vault without .md, no [[…]].
    const written = (await aliceApi.getNote(n.decompression)).content;
    expect(written).toMatch(/^---\nnot-related: \["?Diving\/Dive tables"?\]\n---\n/);
    expect(written.replace(/^---\n[^]*?\n---\n/, ""), "the note's text as it was").toBe(before);
    expect((await aliceApi.getNote(n.tables)).content, "the other note unchanged").not.toContain("not-related");
    await waitSimilarReady(aliceApi, n.decompression, nextIndex);

    expect(await pathsOf(aliceApi, n.decompression), "hidden on the note it was written to").toEqual([n.computer]);
    expect(await pathsOf(aliceApi, n.tables), "hidden on the other note too").toEqual([n.computer]);
    const fromDecompression = await unlinked(n.decompression);
    expect(fromDecompression, "the agent: Dive tables left out").not.toContain(n.tables);
    expect(fromDecompression, "the agent: Dive computer still there").toContain(n.computer);
    expect(await unlinked(n.tables), "the agent, from the other side: Decompression left out").not.toContain(n.decompression);
    // Not a link: without --unlinked, the pair is still close in meaning.
    expect((await agent.sh(`similar "${n.decompression}" "/${vault}"`)).text, "without --unlinked").toContain(n.tables);

    const undone = await notRelated(aliceApi, vault, n.decompression, n.tables, true);
    expect(undone.status(), await undone.text()).toBe(200);
    expect((await aliceApi.getNote(n.decompression)).content, "the entry gone, and its frontmatter with it").toBe(before);
    await waitSimilarReady(aliceApi, n.decompression, nextIndex);
    expect(await pathsOf(aliceApi, n.decompression), "suggested again").toEqual([n.computer, n.tables].sort());
    expect(await unlinked(n.tables), "the agent sees the pair again").toContain(n.decompression);
  } finally {
    await agent.dispose();
  }
});

test("similar-api #10: a read member gets the suggestions, but Add link and Not related are refused @similar @search", async ({ aliceApi, bobApi }) => {
  // The first index, plus up to 61 s per password check on the ledger (up to three for the invitation, one for the deletion).
  test.setTimeout(480_000);
  const share = await shareWithBob("Similar", "read");
  try {
    const n = at(share.vault);
    await seedMeaning(aliceApi, share.vault);
    const before = await aliceApi.getNote(n.decompression);
    await waitSimilarReady(aliceApi, n.decompression, firstIndex);

    expect(await pathsOf(bobApi, n.decompression), "Bob reads the suggestions").toEqual([n.computer, n.tables].sort());
    const linked = await link(bobApi, share.vault, n.decompression, n.tables);
    expect(linked.status(), "Add link refused").toBe(403);
    const hidden = await notRelated(bobApi, share.vault, n.decompression, n.tables);
    expect(hidden.status(), "Not related refused").toBe(403);
    expect(await aliceApi.getNote(n.decompression), "the note unchanged").toEqual(before);
  } finally {
    await unshare(share);
  }
});

test("similar-api #11: a note not indexed yet answers unindexed, then ready @similar @search", async ({ aliceApi, vault }) => {
  test.setTimeout(300_000);
  const n = at(vault);
  await seedMeaning(aliceApi, vault);
  await waitSimilarReady(aliceApi, n.decompression, firstIndex);

  // Asked right after its write: the index embeds it in the background, after the write has answered. A race the
  // request wins (seen in 14 of 14 runs on the search stack, arctic-m on the CPU), not a guarantee.
  const fresh = `/${vault}/Diving/Ascent.md`;
  const decompression = (await aliceApi.getNote(n.decompression)).content;
  await aliceApi.putNote(fresh, decompression.replace("# Decompression stops", "# Ascent"));
  const first = await similarNotes(aliceApi, fresh);
  expect(first, "not indexed yet").toEqual({ state: "unindexed", notes: [], pending: 0 });
  await waitSimilarReady(aliceApi, fresh, nextIndex);
  expect((await similarNotes(aliceApi, fresh)).notes.map((s) => s.path), "then suggestions: Decompression's text").toContain(n.decompression);
});

test("similar-api #12: with search by meaning off, the answer is off @similar", async ({ aliceApi, folder }) => {
  const path = `/Work/${folder}/Decompression.md`;
  await aliceApi.putNote(path, "# Decompression stops\n\nThe diver ascends slowly.\n");
  try {
    expect(await similarNotes(aliceApi, path)).toEqual({ state: "off", notes: [], pending: 0 });
  } finally {
    await aliceApi.deleteNote(path);
  }
});

