// SPDX-License-Identifier: AGPL-3.0-or-later
// Discreet vaults in 0.7.0's places that need search by meaning (spec 2026-11-23-tests-0-7-0-design.md §2, plan Task 8
// with its review's points 3, 6 and 9): with Home hidden, nothing of it shows, in the answers as on the screen. Each test
// signs in a session of its own, enters the PIN at most twice and ends hidden (`endHidden` in `finally`): once through
// the vault menu, to write Home's notes for the test while shown, and once through the call the PIN dialog makes, to
// remove them again at the end. Its Home notes lie below /Home/<folder>/, its Work notes below /Work/<folder>/.
//
// The notes are on beekeeping, a topic no other test writes (plan review point 6), inline: a copy of a shared fixture
// in another test's folder would score 1.00 against ours. Two Work notes (Hive inspection, Brood frames) and one Home note
// (Swarm) on it, close in meaning to each other, no links between them (measured with arctic-m: Hive inspection to Brood
// frames 0.83, to Swarm 0.57, above Similar notes' floor of 0.5, so in one vault Swarm would be suggested); the graph's
// test adds a second Home note (Honey harvest) that links Swarm, so Home's local graph has an edge to measure.
//
// An agent's key and discreet vaults (discreet vaults design, "No change to … API keys"; UserCredential in
// src/Kumbu.Server/Vaults/Credentials.cs carries a DiscreetFilter for a web session only): a key reads Home exactly when
// it was granted Home, whatever a web session shows; only the web app hides discreet vaults, its console included. So
// the hidden view is asserted in the session (the console's API and its page), and the key's view as the design has it.
//
// Promises in scope, and the test that covers each:
// 1. `similar` over every vault, its default scope (knowledge synthesis spec): while hidden, the session's console
//    names nothing of Home, in `similar <Work note>` and `semsearch`, and `similar` on the Home note answers as for a
//    vault that doesn't exist; shown, the Home note is a hit of `similar <Work note>` (the credential then opens Home
//    like any other vault); an agent's key granted Home reads it while the session hides it → "discreet-0-7-0
//    similar: …"
// 2. Similar notes (link prediction spec; plan review point 9): while hidden, `GET /api/similar/Home/<note>` answers
//    as for a made-up vault (status and body); a Work note's Links tab shows its Work neighbour and nothing of Home,
//    nor do the answers the page got → "discreet-0-7-0 similar notes: …"
// 3. The graph with its suggestions (graph out of preview spec): while hidden, `/api/graph/similarity` and
//    `/api/graph/local` on the Home note answer as for a made-up vault; the Work note's graph with its Suggestions layer
//    shows its Work neighbour and nothing of Home → "discreet-0-7-0 graph: …"
// Left out here: the Published window (`@overview`) and the offline copy's vault list (`@offline`), written on the stack
// with search off; the phone (these places' hiding is the server's, which knows no viewport).
import { request, type BrowserContext, type Page, type Response } from "@playwright/test";
import { Api, Mcp } from "../support/api";
import { baseURL, secrets } from "../support/env";
import { firstIndex, nextIndex, similarNotes, waitIndexed } from "../support/search";
import { endHidden, hideDiscreet, reservePin, revealDiscreet } from "../support/session";
import { expect, shot, shotsDir070, test } from "../support/test";
import { dismissInstallHint } from "../calendar/helpers";

test.use({ shotsDir: shotsDir070 });

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;
const titleOf = (path: string) => path.slice(path.lastIndexOf("/") + 1).replace(/\.md$/, "");

/** The test's notes, each title carrying the folder's random part, so nothing else on the stack matches it. */
function notesOf(folder: string) {
  const id = folder.slice(-6);
  return {
    id,
    hive: `/Work/${folder}/Hive inspection ${id}.md`,
    brood: `/Work/${folder}/Brood frames ${id}.md`,
    swarm: `/Home/${folder}/Swarm ${id}.md`,
    harvest: `/Home/${folder}/Honey harvest ${id}.md`,
  };
}

const texts = {
  hive: "# Hive inspection\n\nEvery week in summer the beekeeper opens the hive, lifts out the brood frames one by one and looks for the queen, her eggs and the capped brood.\n\nA puff from the smoker keeps the bees calm. A crowded colony gets another honey super before it builds queen cells and swarms.\n",
  brood: "# Brood frames\n\nOnce a week the beekeeper lights the smoker, opens the hive and lifts out each brood frame, looking for the queen, fresh eggs and capped brood.\n\nIf the colony is crowded and starts queen cells, she adds a honey super so the bees have room and don't swarm.\n",
  swarm: "# Catching a swarm\n\nIn May the bees in my hive raised new queen cells, and half the colony swarmed with the old queen into the apple tree.\n\nI shook the cluster of bees into a box and hived them on fresh frames. Next spring I give the colony its honey supers earlier, so it has room and doesn't swarm.\n",
};

const harvestText = (folder: string, id: string) =>
  `# Honey harvest\n\nIn August the honey supers come off the hive; the frames are uncapped and spun in the extractor.\n\nThe bees of [[${folder}/Swarm ${id}]] gave the most honey.\n`;

/**
 * The session's own API, a request context with its cookie: what the session shows (Home, once the PIN is entered) is
 * what this one is answered, as discreet mode belongs to the session.
 */
async function apiOf(context: BrowserContext) {
  const state = await context.storageState();
  return Api.from(await request.newContext({ baseURL, storageState: state, extraHTTPHeaders: { Origin: baseURL } }), "alice");
}

/** Status and body, for comparing a hidden vault's answer with a made-up vault's. */
async function answerOf(api: Api, url: string) {
  const response = await api.send("GET", url);
  return { status: response.status(), body: await response.text() };
}

/** A hidden Home answers as a vault that doesn't exist: the same status, the same body but the name. */
async function expectAsMissing(api: Api, url: (vault: string) => string, made: string) {
  const hidden = await answerOf(api, url("Home"));
  const missing = await answerOf(api, url(made));
  expect(missing.status, "a made-up vault's answer").toBe(404);
  expect(hidden, `${url("Home").split("?")[0]} while Home is hidden`).toEqual({ status: missing.status, body: missing.body.replaceAll(made, "Home") });
}

/** The page's answers from the similar, graph and console endpoints, for "nothing of Home in what the page got". */
function answersOf(page: Page) {
  const bodies: Promise<string>[] = [];
  page.on("response", (r: Response) => {
    const path = new URL(r.url()).pathname;
    if (/^\/api\/(similar|graph|console)/.test(path)) bodies.push(r.text().catch(() => ""));
  });
  return async () => (await Promise.all(bodies)).join("\n");
}

/**
 * A fresh session of Alice's on her Work note, Home shown through the vault menu (the first PIN entry), the test's
 * Home notes written in it and every note embedded. `written` lists the Home notes, for `removeHome`.
 */
async function start(freshSession: (user: "alice") => Promise<{ context: BrowserContext; page: Page }>, aliceApi: Api, folder: string, home: string[], written: string[]) {
  const n = notesOf(folder);
  await aliceApi.putNote(n.hive, texts.hive);
  await aliceApi.putNote(n.brood, texts.brood);
  const { context, page } = await freshSession("alice");
  await dismissInstallHint(page);
  await page.goto(noteUrl(n.hive));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const session = await apiOf(context);
  return {
    context,
    page,
    session,
    n,
    async showAndWrite() {
      await revealDiscreet(page);
      await expect(page.getByRole("dialog", { name: "More vaults" })).toHaveCount(0);
      for (const path of home) {
        written.push(path);
        await session.putNote(path, path === n.swarm ? texts.swarm : harvestText(folder, n.id));
      }
      await waitIndexed(session, [n.hive], firstIndex);
      await waitIndexed(session, [n.brood, ...home], nextIndex);
    },
  };
}

/** For `finally`: the Home notes written, removed while shown (the second PIN entry, the call the PIN dialog makes). */
async function removeHome(session: Api | undefined, written: string[]) {
  if (!session || written.length === 0) return;
  await reservePin();
  const shown = await session.send("POST", "/api/account/discreet/reveal", { data: { pin: secrets().pin, minutes: null } });
  expect(shown.status(), "shown again to remove the test's Home notes").toBe(200);
  for (const path of written) await session.deleteNote(path);
}

async function removeWork(aliceApi: Api, folder: string) {
  const n = notesOf(folder);
  for (const path of [n.hive, n.brood]) await aliceApi.deleteNote(path).catch(() => undefined);
}

/** The note paths `similar`'s or `semsearch`'s answer lists, in its order. */
const hitsOf = (text: string) => [...text.matchAll(/^\d+\. (\/[^#\n]+?\.md)(?:#[^\n]*?)?  lines /gm)].map((m) => m[1]);

test("discreet-0-7-0 similar: hidden, the console's similar over every vault and semsearch name nothing of Home; shown, the Home note is a hit; an agent's key granted Home reads it @synthesis @search @serial @discreet", async ({ freshSession, aliceApi, folder }, testInfo) => {
  test.setTimeout(480_000);
  const written: string[] = [];
  const s = await start(freshSession, aliceApi, folder, [notesOf(folder).swarm], written);
  const { page, session, n } = s;
  let agent: Mcp | undefined;
  try {
    await s.showAndWrite();
    const ask = `similar "${n.hive}" -n 50`;
    const topic = `semsearch "beekeeper hive queen swarm honey supers brood frames" -n 50`;

    // Shown: the session's credential opens Home like any other vault, so the default scope takes it in.
    const shownSimilar = await session.console(ask);
    expect(shownSimilar.isError, shownSimilar.text.split("\n")[0]).toBe(false);
    expect(hitsOf(shownSimilar.text), "shown: Work's neighbour and the Home note are hits").toEqual(expect.arrayContaining([n.brood, n.swarm]));
    expect(hitsOf((await session.console(topic)).text), "shown: semsearch finds the Home note").toContain(n.swarm);
    // An agent's key granted Home, made while Home is shown (a key on Work too stays listed, and revocable, once hidden).
    agent = await Mcp.create(session, `e2e discreet ${n.id}`, { Work: "read", Home: "read" });

    await hideDiscreet(page);

    // Hidden: the session's console, in its answers' text, names nothing of Home.
    const hidden = await session.console(ask);
    expect(hidden.isError, hidden.text.split("\n")[0]).toBe(false);
    expect(hitsOf(hidden.text), "hidden: Work's neighbour is still a hit").toContain(n.brood);
    expect(hidden.text, "hidden: similar over every vault names nothing of Home").not.toMatch(/\/Home\/|Swarm/);
    const words = await session.console(topic);
    expect(hitsOf(words.text), "hidden: semsearch still finds Work's notes").toEqual(expect.arrayContaining([n.hive, n.brood]));
    expect(words.text, "hidden: semsearch names nothing of Home").not.toMatch(/\/Home\/|Swarm/);
    // The Home note itself answers as a note of a vault that doesn't exist.
    const made = `Nowhere${n.id}`;
    const ofHome = await session.console(`similar "${n.swarm}"`);
    const ofNowhere = await session.console(`similar "${n.swarm.replace("/Home/", `/${made}/`)}"`);
    expect(ofNowhere.isError, "a made-up vault's note").toBe(true);
    expect(ofHome, "hidden: similar on the Home note").toEqual({ text: ofNowhere.text.replaceAll(made, "Home"), isError: true });

    // The console's page, the session's view on the screen.
    const got = answersOf(page);
    await page.goto("/console");
    const output = page.getByRole("log", { name: "Console output" });
    const command = page.getByRole("textbox", { name: "Command" });
    await expect(command).toBeEnabled();
    await command.fill(ask);
    await command.press("Enter");
    await expect(output).toContainText(`similar to ${n.hive}, in every vault:`);
    await expect(output).toHaveAttribute("aria-busy", "false");
    await expect(output).toContainText(titleOf(n.brood));
    await expect(output, "hidden: the console's page shows nothing of Home").not.toContainText(/\/Home\/|Swarm/);
    expect(await got(), "hidden: nothing of Home in the console's answers").not.toMatch(/\/Home\/|Swarm/);
    await shot(page, testInfo, "discreet");

    // The agent's key: Home granted, so read whatever the session shows (discreet vaults design).
    const byKey = await agent.sh(ask);
    expect(hitsOf(byKey.text), "an agent's key granted Home reads it while the session hides it").toContain(n.swarm);
  } finally {
    await removeHome(session, written).catch((e: Error) => test.info().annotations.push({ type: "cleanup", description: `Home notes not removed: ${e.message}` }));
    await agent?.dispose().catch(() => undefined);
    await endHidden(page);
    await session.dispose();
    await removeWork(aliceApi, folder);
  }
});

test("discreet-0-7-0 similar notes: hidden, /api/similar on a Home note answers as for a made-up vault, and a Work note's Links tab shows nothing of Home @similar @search @serial @discreet", async ({ freshSession, aliceApi, folder }) => {
  test.setTimeout(480_000);
  const written: string[] = [];
  const s = await start(freshSession, aliceApi, folder, [notesOf(folder).swarm], written);
  const { page, session, n } = s;
  try {
    await s.showAndWrite();
    // Shown, the Home note's suggestions are answered (its vault has none to give: Swarm is Home's only note here).
    expect((await similarNotes(session, n.swarm)).state, "shown: the Home note's suggestions are answered").toBe("ready");
    await hideDiscreet(page);

    const made = `Nowhere${n.id}`;
    await expectAsMissing(session, (vault) => `/api/similar/${vault}/${encodeURIComponent(folder)}/${encodeURIComponent(titleOf(n.swarm))}.md`, made);

    // The Work note's Links tab: its Work neighbour, and nothing of Home.
    const got = answersOf(page);
    await page.reload();
    await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
    const section = page.getByRole("region", { name: "Similar notes" });
    const button = section.getByRole("button", { name: "Similar notes", exact: true });
    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "true");
    const rows = section.getByRole("listitem");
    await expect(rows.first()).toBeVisible();
    expect(await rows.allInnerTexts(), "hidden: the Links tab's rows").toEqual([expect.stringContaining(titleOf(n.brood))]);
    await expect(section, "hidden: the Links tab shows nothing of Home").not.toContainText(/Home|Swarm/);
    expect(await got(), "hidden: nothing of Home in the page's answers").not.toMatch(/\/Home\/|Swarm/);
  } finally {
    await removeHome(session, written).catch((e: Error) => test.info().annotations.push({ type: "cleanup", description: `Home notes not removed: ${e.message}` }));
    await endHidden(page);
    await session.dispose();
    await removeWork(aliceApi, folder);
  }
});

test("discreet-0-7-0 graph: hidden, /api/graph/similarity and /api/graph/local on a Home note answer as for a made-up vault, and the Work note's graph with its suggestions shows nothing of Home @graph @search @serial @discreet", async ({ freshSession, aliceApi, folder }) => {
  test.setTimeout(480_000);
  const written: string[] = [];
  const all = notesOf(folder);
  const s = await start(freshSession, aliceApi, folder, [all.swarm, all.harvest], written);
  const { page, session, n } = s;
  try {
    await s.showAndWrite();
    const local = (vault: string) => `/api/graph/local?path=${encodeURIComponent(n.harvest.replace("/Home/", `/${vault}/`))}`;
    const similarity = (vault: string) => `/api/graph/similarity?path=${encodeURIComponent(n.harvest.replace("/Home/", `/${vault}/`))}`;
    // Shown: Home's graph is drawn and its edge measured.
    expect((await answerOf(session, local("Home"))).status, "shown: Home's local graph").toBe(200);
    const measured = await session.json<{ state: string; pairs: { a: string; b: string; score: number | null }[] }>("GET", similarity("Home"));
    expect(measured.state, "shown: Home's edges measured").toBe("ready");
    expect(measured.pairs.length, "shown: Honey harvest's link to Swarm").toBe(1);
    await hideDiscreet(page);

    const made = `Nowhere${n.id}`;
    await expectAsMissing(session, similarity, made);
    await expectAsMissing(session, local, made);

    // The Work note's graph, its Suggestions layer on: its Work neighbour, nothing of Home.
    const got = answersOf(page);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.reload();
    await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
    await page.getByRole("button", { name: "Graph", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: titleOf(n.hive) });
    await expect(dialog).toBeVisible();
    const drawing = dialog.getByRole("group", { name: `Graph of ${titleOf(n.hive)}` });
    const switched = dialog.getByRole("button", { name: "Suggestions", exact: true });
    await switched.click();
    await expect(switched).toHaveAttribute("aria-pressed", "true");
    const suggested = drawing.getByRole("button", { name: /^Suggested link: / });
    await expect(suggested).toHaveCount(2);
    await expect(suggested).toHaveAccessibleName(`Suggested link: ${titleOf(n.brood)}`);
    await expect(dialog, "hidden: the graph shows nothing of Home").not.toContainText(/Swarm|Honey harvest/);
    const labels = await drawing.getByRole("button").evaluateAll((els) => els.map((e) => e.getAttribute("aria-label") ?? ""));
    expect(labels.filter((l) => /Swarm|Honey harvest|Home/.test(l)), "hidden: no node or suggestion of Home in the drawing").toEqual([]);
    expect(await got(), "hidden: nothing of Home in the page's answers").not.toMatch(/\/Home\/|Swarm|Honey harvest/);
  } finally {
    await removeHome(session, written).catch((e: Error) => test.info().annotations.push({ type: "cleanup", description: `Home notes not removed: ${e.message}` }));
    await endHidden(page);
    await session.dispose();
    await removeWork(aliceApi, folder);
  }
});
