// SPDX-License-Identifier: AGPL-3.0-or-later
// An external embedding provider through MCP (README "Search by meaning"; plan of the 0.7.0 tests, Task 9): the stack's
// `tensorx` provider (openai-compatible, TensorX's Qwen3-Embedding 8B at 1024 values, compose.search.yaml), whose key
// file holds a placeholder that the VM's egress proxy replaces with the real key. Two vaults of this file's own, made in
// `beforeAll` and deleted in `afterAll`: one switched to `tensorx` with `PUT /api/vaults/{vault}/embeddings`, one left on
// the server's model (`internal`, the default). Their notes are on beekeeping, a topic no other test writes, plus a cake
// as the clear case far from it; they are written through the web app's API and read by an agent whose key has these two
// vaults only, so "every vault" is these two. Every request here goes to a paid service: a few short notes, one chunk each.
//
// Promises in scope, and the test that covers each:
// 1. A vault switched to `tensorx` is embedded through it: the vault says `tensorx`, the provider is available,
//    `semsearch --meaning` ranks the bee notes above the cake, `similar` ranks a bee note above the cake
//    → "a vault switched to tensorx is embedded through it …".
// 2. `similar` never compares vectors of two models (knowledge synthesis spec §2, §3.8 "other-model notes counted in the
//    footer, never compared"): a source in the `tensorx` vault and candidates in the `internal` one, and the other way
//    round, gives no hits and counts the candidates in the footer; over every vault, the other vault's bee note, the
//    closest by topic, is not ranked, and is counted → "similar never compares two models …".
// Left out:
// - BYOK (a person's own TensorX key): the browser, web/e2e/tensorx/tensorx.spec.ts.
// - The provider's pause on 429 and 5xx, and the fallback to words: needs a failing upstream, xUnit (OpenAiCompatibleDriver).
// - What TensorX keeps: its promise, not something a test can see.
import { randomBytes } from "node:crypto";
import { Api, Mcp } from "../support/api";
import { secrets } from "../support/env";
import { firstIndex, waitIndexed } from "../support/search";
import { reservePassword } from "../support/session";
import { expect, test } from "./fixtures";

// waitIndexed's bound (the first index through the provider) needs room within the test.
test.describe.configure({ timeout: 240_000 });

const id = randomBytes(3).toString("hex");
const external = `Tensorx-${id}`;
const local = `Internal-${id}`;

const notes = {
  hive: `/${external}/Bees/Hive.md`,
  swarm: `/${external}/Bees/Swarm.md`,
  cake: `/${external}/Bees/Cake.md`,
  honey: `/${local}/Bees/Honey.md`,
  localCake: `/${local}/Bees/Cake.md`,
};
const cake =
  "# Cake\n\nCream the soft butter with the sugar until pale, beat in the eggs, fold in the flour and the cocoa, and bake the batter in the oven at 180 degrees for forty minutes.\n";
const texts: Record<keyof typeof notes, string> = {
  hive: "# Hive\n\nThe beekeeper opens the hive and lifts out the frames to check the brood, the queen and the stores of honey, with a little smoke to calm the bees.\n",
  swarm: "# Swarm\n\nIn late spring a colony of bees raises new queens, and the old queen leaves the hive with half the workers; the beekeeper catches the swarm in a box.\n",
  cake,
  honey: "# Honey\n\nThe beekeeper takes the full frames from the hive, cuts the wax cappings off the comb and spins the honey out of it in an extractor.\n",
  localCake: cake,
};

const plainFooter = '(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; cat "path#Section" reads one)';
const otherModel = (n: number) => `${n} ${n === 1 ? "note is" : "notes are"} embedded by another model and not compared`;

/** `similar`'s answer: the heading, the hits' paths in order, the footer. */
function parse(text: string) {
  const lines = text.split("\n");
  const hits = lines.slice(1, -1).flatMap((l) => /^\d+\. (.+?\.md)#/.exec(l)?.[1] ?? []);
  return { heading: lines[0], hits, footer: lines[lines.length - 1] };
}

let agent: Mcp | undefined;
let agentOwner: Api | undefined;

test.beforeAll(async () => {
  test.setTimeout(240_000);
  const api = await Api.as("alice");
  try {
    await api.json("POST", "/api/vaults", { name: external });
    await api.json("POST", "/api/vaults", { name: local });
    // The switch before any note, so that nothing of the vault is embedded by the server's model first.
    const switched = await api.json<{ embeddings: string }>("PUT", `/api/vaults/${encodeURIComponent(external)}/embeddings`, { provider: "tensorx" });
    expect(switched.embeddings).toBe("tensorx");
    for (const [name, path] of Object.entries(notes)) await api.putNote(path, texts[name as keyof typeof notes]);
    await waitIndexed(api, Object.values(notes), firstIndex);
    // An agent with these two vaults only: its "every vault" is them.
    agentOwner = await Api.as("alice");
    agent = await Mcp.create(agentOwner, `e2e tensorx ${id}`, { [external]: "read", [local]: "read" });
  } finally {
    await api.dispose();
  }
});

test.afterAll(async () => {
  test.setTimeout(240_000);
  if (agent) await agent.dispose();
  if (agentOwner) await agentOwner.dispose();
  const api = await Api.as("alice");
  try {
    const names = (await api.json<{ name: string }[]>("GET", "/api/vaults")).map((v) => v.name);
    for (const vault of [external, local].filter((v) => names.includes(v))) {
      await reservePassword("alice");
      await api.json("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { password: secrets().passwords.alice, confirm: vault });
    }
  } finally {
    await api.dispose();
  }
});

async function sh(command: string) {
  const answer = await agent!.sh(command);
  expect(answer.isError, answer.text).toBe(false);
  return answer.text;
}

test("a vault switched to tensorx is embedded through it: semsearch and similar rank by meaning @tensorx @search", async ({ aliceApi }) => {
  // Read back through a second call: the vault list and the providers.
  const vaults = await aliceApi.json<{ name: string; embeddings: string }[]>("GET", "/api/vaults");
  expect(vaults.find((v) => v.name === external)?.embeddings).toBe("tensorx");
  expect(vaults.find((v) => v.name === local)?.embeddings).toBe("internal");
  const providers = await aliceApi.json<{ name: string; external: boolean; available: boolean }[]>("GET", "/api/embeddings");
  expect(providers.find((p) => p.name === "tensorx")).toMatchObject({ external: true, available: true });

  // By meaning: the query shares no content word with any note (--meaning adds a bonus per query word, Semsearch.cs:43-44).
  const found = await sh(`semsearch --meaning -l -n 10 insects abandoning their home in spring with a new ruler /${external}`);
  const ranked = found.split("\n").flatMap((l) => /^\d+\. (.+?\.md)#/.exec(l)?.[1] ?? []);
  expect(ranked[0], found).toBe(notes.swarm);
  expect(ranked, found).toEqual([notes.swarm, notes.hive, notes.cake].slice(0, ranked.length));
  expect(found.split("\n").at(-1)).toBe('(ranked by meaning; cat "path#Section" reads one)');

  const near = parse(await sh(`similar "${notes.hive}" /${external}`));
  expect(near.heading).toBe(`similar to ${notes.hive}, below /${external}: 2 notes`);
  expect(near.hits).toEqual([notes.swarm, notes.cake]);
  expect(near.footer).toBe(plainFooter);
});

test("similar never compares two models: candidates of the other vault's model are counted, not ranked @tensorx @search", async () => {
  // A tensorx source, the internal vault as the scope: nothing to compare, both notes counted.
  const across = parse(await sh(`similar "${notes.hive}" /${local}`));
  expect(across.heading).toBe(`similar to ${notes.hive}, below /${local}: 0 notes`);
  expect(across.hits).toEqual([]);
  expect(across.footer).toBe(`(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; ${otherModel(2)})`);

  // The other way round: an internal source, the tensorx vault as the scope.
  const back = parse(await sh(`similar "${notes.honey}" /${external}`));
  expect(back.heading).toBe(`similar to ${notes.honey}, below /${external}: 0 notes`);
  expect(back.hits).toEqual([]);
  expect(back.footer).toBe(`(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; ${otherModel(3)})`);

  // Every vault (the agent's two): the source's own vault ranked, the honey note, closest by topic, never.
  const everywhere = parse(await sh(`similar "${notes.hive}"`));
  expect(everywhere.heading).toMatch(/: 2 notes$/);
  expect(everywhere.hits).toEqual([notes.swarm, notes.cake]);
  expect(everywhere.footer).toBe(`(by the stored embeddings, nothing sent to an embedding provider; distance counts links either way; ${otherModel(2)}; cat "path#Section" reads one)`);
});
