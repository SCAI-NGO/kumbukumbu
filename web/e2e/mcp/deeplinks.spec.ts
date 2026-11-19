// SPDX-License-Identifier: AGPL-3.0-or-later
// Note deep links (docs/superpowers/specs/2026-11-17-note-deep-links-design.md; PR #229), through MCP.
//
// Promises in scope, and the test that covers each:
// 1. The URL is `<origin>/n/<vault id>/<path inside the vault>`, each segment percent-encoded; the origin is
//    KUMBU_PUBLIC_URL (the stack sets http://localhost:8080, stack/env.sh) → every test below compares whole lines.
// 2. Wherever a version block is printed, the link follows it on its own line, `link to <path>: <url>`: `write`,
//    `edit`, `cat` of a note and of a section (the link leads to the note) → "write, edit and cat print the link …";
//    `replace`, one per changed note → "replace prints a link for each note …"; an archived note (`archive`, then its
//    `cat`) → "an archived note's answers carry its link".
// 3. Not in search results: `rg` prints no link → "replace prints a link for each note …".
// 4. At most ten links per answer, then `…and N more links: cat a note for its link`; every version block stays
//    → "an answer with eleven versions carries ten links …".
// 5. The id is the vault's, the same for the owner and a member whatever each calls the vault
//    → "the link is the same for Alice and for Bob …" (Bob renames his alias, and back in `finally`).
// 6. `GET /api/vaults` names each vault's id → the id every test expects comes from it (and Bob's from his own list).
// 7. KUMBU_PUBLIC_URL wins over the request's origin → "the link's origin is KUMBU_PUBLIC_URL, not the request's"
//    (a write through http://127.0.0.1:8080).
// 8. The tool descriptions say once that the link opens the note after sign-in and is the thing to show the user
//    (Sh/Manual.cs) → "the descriptions of the tools that carry a link say so".
// Left out: the upload's answer (@upload, Task 10); a file has no version block in `cat`, so no link there (the browser
// test opens a file's link built in the spec's form).
import { request } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, Mcp } from "../support/api";
import { baseURL } from "../support/env";
import { shareWithBob, unshare, type Share } from "../support/share";
import { expect, test } from "./fixtures";

/** KUMBU_PUBLIC_URL on the stack (web/e2e/stack/env.sh). */
const publicUrl = "http://localhost:8080";

const vaultId = async (api: Api, name: string) => {
  const vault = (await api.json<{ id: string; name: string }[]>("GET", "/api/vaults")).find((v) => v.name === name);
  expect(vault, `${name} in the vault list`).toBeDefined();
  return vault!.id;
};

/** The spec's link for `/<vault>/<path…>` in the vault with that id. */
const linkFor = (id: string, path: string) => `${publicUrl}/n/${id}/${path.split("/").slice(2).map(encodeURIComponent).join("/")}`;

const lines = (text: string) => text.split("\n").filter((l) => l.length > 0);

/** The `version of` and `link to` lines of an answer, in their order. */
const blocks = (text: string) => lines(text).filter((l) => l.startsWith("version of ") || l.startsWith("link to ") || l.startsWith("…and "));

/** Each path's version line, then its link line: what an answer with a version block per path ends with. */
const linked = (id: string, paths: string[]) => paths.flatMap((p) => [expect.stringMatching(new RegExp(`^version of ${escape(p)}: \\w+$`)), `link to ${p}: ${linkFor(id, p)}`]);

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

test("deeplinks: write, edit and cat print the link after the note's version @deeplinks", async ({ mcp, aliceApi, folder }) => {
  const work = await vaultId(aliceApi, "Work");
  // Spaces and umlauts: each segment encoded on its own.
  const path = `/Work/${folder}/Garten Ü/Rosen Größe.md`;
  const link = `link to ${path}: ${publicUrl}/n/${work}/${folder}/Garten%20%C3%9C/Rosen%20Gr%C3%B6%C3%9Fe.md`;
  expect(linkFor(work, path)).toBe(link.slice(`link to ${path}: `.length));

  const written = await mcp.call("write", { path, content: "# Rosen\n\n## Pflege\n\nwater\n" });
  expect(written.isError, written.text).toBe(false);
  expect(blocks(written.text)).toEqual(linked(work, [path]));
  const version = written.text.match(/^version of .*: (\w+)$/m)![1];

  const edited = await mcp.call("edit", { path, version, old_text: "water", new_text: "wasser" });
  expect(edited.isError, edited.text).toBe(false);
  expect(blocks(edited.text)).toEqual(linked(work, [path]));

  const read = await mcp.sh(`cat "${path}"`);
  expect(read.isError, read.text).toBe(false);
  expect(lines(read.text).slice(-1)).toEqual([link]);
  expect(blocks(read.text)).toEqual(linked(work, [path]));

  // A section's `cat`: the link leads to the note, not to the section.
  const section = await mcp.sh(`cat "${path}#Pflege"`);
  expect(section.isError, section.text).toBe(false);
  expect(lines(section.text)[0]).toBe("## Pflege");
  expect(blocks(section.text)).toEqual(linked(work, [path]));
});

test("deeplinks: replace prints a link for each note it changes, rg prints none @deeplinks", async ({ mcp, aliceApi, folder }) => {
  const work = await vaultId(aliceApi, "Work");
  const paths = [`/Work/${folder}/Alpha.md`, `/Work/${folder}/Beta.md`];
  for (const path of paths) expect((await mcp.call("write", { path, content: "water\n" })).isError).toBe(false);

  const preview = await mcp.call("replace", { path: `/Work/${folder}`, old_text: "water", new_text: "wasser" });
  expect(preview.isError, preview.text).toBe(false);
  // The preview prints no version block, so no link.
  expect(blocks(preview.text)).toEqual([]);
  const plan = preview.text.match(/^plan: (\w+)$/m)?.[1];
  expect(plan, preview.text).toBeDefined();

  const applied = await mcp.call("replace", { path: `/Work/${folder}`, old_text: "water", new_text: "wasser", plan });
  expect(applied.isError, applied.text).toBe(false);
  expect(lines(applied.text)[0]).toBe("replaced 2 occurrences in 2 notes");
  expect(blocks(applied.text)).toEqual(linked(work, paths));

  const found = await mcp.sh(`rg wasser /Work/${folder}`);
  expect(found.isError, found.text).toBe(false);
  expect(lines(found.text)).toEqual(paths.map((p) => `${p}:1:wasser`));
});

test("deeplinks: an answer with eleven versions carries ten links, then the line for the rest @deeplinks", async ({ mcp, aliceApi, folder }) => {
  const work = await vaultId(aliceApi, "Work");
  const paths = Array.from({ length: 11 }, (_, i) => `/Work/${folder}/Note ${String(i + 1).padStart(2, "0")}.md`);
  for (const path of paths) expect((await mcp.call("write", { path, content: "water\n" })).isError).toBe(false);

  const preview = await mcp.call("replace", { path: `/Work/${folder}`, old_text: "water", new_text: "wasser" });
  const plan = preview.text.match(/^plan: (\w+)$/m)?.[1];
  expect(plan, preview.text).toBeDefined();
  const applied = await mcp.call("replace", { path: `/Work/${folder}`, old_text: "water", new_text: "wasser", plan });
  expect(applied.isError, applied.text).toBe(false);

  // Every version block stays; the first ten get their link.
  const versions = lines(applied.text).filter((l) => l.startsWith("version of "));
  expect(versions.map((l) => l.replace(/: \w+$/, ""))).toEqual(paths.map((p) => `version of ${p}`));
  expect(lines(applied.text).filter((l) => l.startsWith("link to "))).toEqual(paths.slice(0, 10).map((p) => `link to ${p}: ${linkFor(work, p)}`));
  expect(lines(applied.text).at(-1)).toBe("…and 1 more link: cat a note for its link");
});

test("deeplinks: an archived note's answers carry its link @deeplinks", async ({ mcp, aliceApi, folder }) => {
  const work = await vaultId(aliceApi, "Work");
  const path = `/Work/${folder}/Old.md`;
  expect((await mcp.call("write", { path, content: "# Old\n" })).isError).toBe(false);
  const archived = await mcp.sh(`archive ${path}`);
  // The archive adds the time to the name: read it from the answer.
  const moved = archived.text.match(/^moved \S+ to (\/Work\/_archive\/.+\.md); /m)?.[1];
  try {
    expect(archived.isError, archived.text).toBe(false);
    expect(moved, archived.text).toMatch(new RegExp(`^/Work/_archive/${folder}/Old \\d{4}-\\d{2}-\\d{2} \\d{6}\\.md$`));
    expect(blocks(archived.text)).toEqual(linked(work, [moved!]));
    const read = await mcp.sh(`cat "${moved}"`);
    expect(read.isError, read.text).toBe(false);
    expect(blocks(read.text)).toEqual(linked(work, [moved!]));
  } finally {
    if (moved) {
      const removed = await mcp.sh(`rm "${moved}"`);
      expect(removed.isError, removed.text).toBe(false);
    }
  }
});

const linkSentence = "The answer carries the note's link into the web app (it opens after sign-in): show it to the user.";
const catLine =
  "cat <path>[#section] [<path>…]  print a note or one section (several paths: each under a header); its version token comes as a separate block, followed by its link into the web app (show it to the user)";

test("deeplinks: the descriptions of the tools that carry a link say so @deeplinks", async ({ mcp }) => {
  const { status, message } = await mcp.raw("tools/list", {});
  expect(status).toBe(200);
  const tools = (message?.result as { tools: { name: string; description?: string }[] }).tools;
  const description = (name: string) => tools.find((t) => t.name === name)?.description ?? "";
  for (const name of ["write", "edit", "replace"]) {
    expect(description(name).split("\n").map((l) => l.trim()), `${name}'s description`).toContain(linkSentence);
  }
  // The sh tool's names it on cat's line, where the link comes.
  expect(description("sh").split("\n").map((l) => l.trim()), "sh's description").toContain(catLine);
});

test("deeplinks: the link's origin is KUMBU_PUBLIC_URL, not the request's @deeplinks", async ({ aliceApi, folder }) => {
  const work = await vaultId(aliceApi, "Work");
  // A key of Alice's, used through another host for the same server; revoked by dispose.
  const body = { data: { name: `e2e deeplinks origin ${randomBytes(3).toString("hex")}`, vaults: { Work: "write" }, expiresInDays: 30 } };
  let response = await aliceApi.send("POST", "/api/keys", body);
  if (response.status() === 401) {
    await aliceApi.confirmPassword();
    response = await aliceApi.send("POST", "/api/keys", body);
  }
  // Never the body: it holds the key.
  expect(response.ok(), `POST /api/keys: ${response.status()}`).toBe(true);
  const created = (await response.json()) as { key: string; apiKey: { id: string } };
  const otherHost = baseURL.replace("localhost", "127.0.0.1");
  const key = new Mcp(await request.newContext({ baseURL: otherHost }), created.key, { api: aliceApi, id: created.apiKey.id });
  try {
    const path = `/Work/${folder}/Origin.md`;
    const written = await key.call("write", { path, content: "# Origin\n" });
    expect(written.isError, written.text).toBe(false);
    expect(blocks(written.text)).toEqual(linked(work, [path]));
    expect(lines(written.text).at(-1)).toMatch(/^link to \S+: http:\/\/localhost:8080\/n\//);
  } finally {
    await key.dispose();
  }
});

test.describe("deeplinks in a shared vault", () => {
  let share: Share;
  let alice: Api;
  let bob: Api;
  /** Alice's agent, reading the shared vault. */
  let aliceKey: Mcp;

  test.beforeAll(async () => {
    // Keys, invitations and deletions wait on the 5-a-minute password ledger, shared with other workers.
    test.setTimeout(4 * 60_000);
    share = await shareWithBob("Team", "write");
    alice = await Api.as("alice");
    bob = await Api.as("bob");
    aliceKey = await Mcp.create(alice, `e2e deeplinks alice ${randomBytes(3).toString("hex")}`, { [share.vault]: "read" });
  });

  test.afterAll(async () => {
    // Keys, invitations and deletions wait on the 5-a-minute password ledger, shared with other workers.
    test.setTimeout(4 * 60_000);
    await aliceKey?.dispose();
    await alice?.dispose();
    await bob?.dispose();
    if (share) await unshare(share);
  });

  test("deeplinks: the link is the same for Alice and for Bob, who calls the vault something else @deeplinks", async ({ folder }) => {
    const id = await vaultId(alice, share.vault);
    const alias = `Ours-${randomBytes(3).toString("hex")}`;
    await bob.json("PUT", `/api/memberships/${encodeURIComponent(share.membershipId)}/alias`, { alias });
    let bobKey: Mcp | undefined;
    try {
      expect(await vaultId(bob, alias), "Bob's list names the same id").toBe(id);
      bobKey = await Mcp.create(bob, `e2e deeplinks bob ${randomBytes(3).toString("hex")}`, { [alias]: "write" });
      const bobPath = `/${alias}/${folder}/Plan.md`;
      const written = await bobKey.call("write", { path: bobPath, content: "# Plan\n" });
      expect(written.isError, written.text).toBe(false);
      const link = linkFor(id, bobPath);
      expect(link).toBe(`${publicUrl}/n/${id}/${folder}/Plan.md`);
      expect(blocks(written.text)).toEqual(linked(id, [bobPath]));

      // Alice's agent reads the note under her name for the vault and gets the same URL.
      const alicePath = `/${share.vault}/${folder}/Plan.md`;
      const read = await aliceKey.sh(`cat ${alicePath}`);
      expect(read.isError, read.text).toBe(false);
      expect(lines(read.text).at(-1)).toBe(`link to ${alicePath}: ${link}`);
    } finally {
      await bobKey?.dispose();
      await bob.json("PUT", `/api/memberships/${encodeURIComponent(share.membershipId)}/alias`, { alias: share.vault });
    }
  });
});
