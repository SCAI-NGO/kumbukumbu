// SPDX-License-Identifier: AGPL-3.0-or-later
// Discreet vaults in 0.7.0's places that need search by meaning (spec 2026-11-23-tests-0-7-0-design.md §2, plan Task 8
// with its review's points 3, 6 and 9): with Home hidden, nothing of it shows, in the answers as on the screen. Each test
// signs in a session of its own, enters the PIN at most twice and ends hidden (`endHidden`, and the hide call through the
// session's own request context, in `finally`): once through the vault menu, to write Home's notes for the test while
// shown, and once through the call the PIN dialog makes, to remove them again at the end. Its Home notes lie below
// /Home/<folder>/, its other notes below /<vault>/<folder>/, where <vault> is a vault of this file's own (`Bees-<random>`,
// made in `beforeAll`, deleted in `afterAll`): Similar notes and the graph's suggestions are vault-wide, and in the shared
// Work vault any other test's note close enough would join the exact lists asserted here.
//
// The notes are on beekeeping, a topic no other test writes (plan review point 6), inline: a copy of a shared fixture
// would score 1.00 against ours. Two notes in the file's vault (Hive inspection, Brood frames) and one in Home (Swarm),
// close in meaning, no links between them: Brood frames ranks first for Hive inspection, and Swarm, close enough to be
// suggested if it lay in the same vault, would rank second. The graph's test adds a second Home note (Honey harvest)
// that links Swarm, so Home's local graph has an edge to measure. Home's notes carry an id of their own, so an answer
// naming them is caught by that id as well as by their titles and the vault's name.
//
// An agent's key and discreet vaults (discreet vaults design, "No change to … API keys"; UserCredential in
// src/Kumbu.Server/Vaults/Credentials.cs carries a DiscreetFilter for a web session only): a key reads Home exactly when
// it was granted Home, whatever a web session shows; only the web app hides discreet vaults, its console included. So
// the hidden view is asserted in the session (the console's API and its page), and the key's view as the design has it.
//
// Promises in scope, and the test that covers each:
// 1. `similar` over every vault, its default scope (knowledge synthesis spec): while hidden, the session's console
//    names nothing of Home, in `similar <note>` and `semsearch`, and `similar` on the Home note answers as for a vault
//    that doesn't exist; shown, the Home note is a hit of `similar <note>` (the credential then opens Home like any
//    other vault); an agent's key granted Home reads it while the session hides it → "discreet-0-7-0 similar: …"
// 2. Similar notes (link prediction spec; plan review point 9): while hidden, `GET /api/similar/Home/<note>` answers
//    as for a made-up vault (status and body); a note's Links tab in another vault shows its neighbour and nothing of
//    Home, nor do the answers the page got → "discreet-0-7-0 similar notes: …"
// 3. The graph with its suggestions (graph out of preview spec): while hidden, `/api/graph/similarity` and
//    `/api/graph/local` on the Home note answer as for a made-up vault; the other vault's note's graph with its
//    Suggestions layer shows its neighbour and nothing of Home → "discreet-0-7-0 graph: …"
// 4. The Published window (published overview spec; no `@search`: it runs in the phase with search off): a Home note
//    published while shown is a row; hidden, it has no row, nothing of Home is on the page or in GET
//    /api/publications, and the count line and the rows are the server's hidden list → "discreet-0-7-0 published: …".
//    Its reader link is kept in a variable and never read or shown; no screenshot, as in overview.spec.ts.
// Left out here: the offline copy's vault list (`@offline`), in the offline tests; the phone (these places' hiding is
// the server's, which knows no viewport).
import { request, type BrowserContext, type Page, type Response } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, Mcp, encodePath } from "../support/api";
import { baseURL, secrets } from "../support/env";
import { firstIndex, nextIndex, similarNotes, waitIndexed } from "../support/search";
import { endHidden, hideDiscreet, reservePassword, reservePin, revealDiscreet } from "../support/session";
import { expect, shot, shotsDir070, test } from "../support/test";
import { dismissInstallHint } from "../calendar/helpers";

// No failure screenshots: one could show a dialog with a link (the Published test; overview.spec.ts does the same).
test.use({ shotsDir: shotsDir070, screenshot: "off" });

// The file's own vault, for the notes beside Home's.
let vault = "";
test.beforeAll(async () => {
  vault = `Bees-${randomBytes(3).toString("hex")}`;
  const api = await Api.as("alice");
  try {
    await api.json("POST", "/api/vaults", { name: vault });
  } finally {
    await api.dispose();
  }
});
test.afterAll(async () => {
  test.setTimeout(240_000);
  if (!vault) return;
  const api = await Api.as("alice");
  try {
    await reservePassword("alice");
    await api.json("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { password: secrets().passwords.alice, confirm: vault });
  } finally {
    await api.dispose();
  }
});

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;
const titleOf = (path: string) => path.slice(path.lastIndexOf("/") + 1).replace(/\.md$/, "");

/** The test's notes; Home's carry an id of their own (`hid`), which nothing else on the stack holds. */
function notesOf(folder: string) {
  const id = folder.slice(-6);
  const hid = randomBytes(3).toString("hex");
  return {
    id,
    hid,
    hive: `/${vault}/${folder}/Hive inspection ${id}.md`,
    brood: `/${vault}/${folder}/Brood frames ${id}.md`,
    swarm: `/Home/${folder}/Swarm ${hid}.md`,
    harvest: `/Home/${folder}/Honey harvest ${hid}.md`,
  };
}
type Notes = ReturnType<typeof notesOf>;

/** Anything of Home: its vault's path, its notes' titles, their id. */
const ofHome = (n: Notes) => new RegExp(`/Home/|\\bHome\\b|Swarm|Honey harvest|${n.hid}`);

/**
 * The lines of a console answer that name Home. Over every vault, other people's notes are hits too, and their quoted
 * text may say "Home" as a word: there, the word counts only in the heading and the hit lines (the paths), and the
 * rest of Home's marks everywhere.
 */
const homeLines = (text: string, n: Notes) =>
  text.split("\n").filter((line) => new RegExp(`/Home/|Swarm|Honey harvest|${n.hid}`).test(line) || (!/^\s/.test(line) && /\bHome\b/.test(line)));

const texts = {
  hive: "# Hive inspection\n\nEvery week in summer the beekeeper opens the hive, lifts out the brood frames one by one and looks for the queen, her eggs and the capped brood.\n\nA puff from the smoker keeps the bees calm. A crowded colony gets another honey super before it builds queen cells and swarms.\n",
  brood: "# Brood frames\n\nOnce a week the beekeeper lights the smoker, opens the hive and lifts out each brood frame, looking for the queen, fresh eggs and capped brood.\n\nIf the colony is crowded and starts queen cells, she adds a honey super so the bees have room and don't swarm.\n",
  swarm: "# Catching a swarm\n\nIn May the bees in my hive raised new queen cells, and half the colony swarmed with the old queen into the apple tree.\n\nI shook the cluster of bees into a box and hived them on fresh frames. Next spring I give the colony its honey supers earlier, so it has room and doesn't swarm.\n",
};

const harvestText = (folder: string, hid: string) =>
  `# Honey harvest\n\nIn August the honey supers come off the hive; the frames are uncapped and spun in the extractor.\n\nThe bees of [[${folder}/Swarm ${hid}]] gave the most honey.\n`;

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

/** The page's answers from the similar, graph, console and publications endpoints, for "nothing of Home in what the page got". */
function answersOf(page: Page) {
  const bodies: Promise<string>[] = [];
  page.on("response", (r: Response) => {
    const path = new URL(r.url()).pathname;
    // The overview only: the owner's read of one publication carries its reader link.
    if (/^\/api\/(similar|graph|console)|^\/api\/publications$/.test(path)) bodies.push(r.text().catch(() => ""));
  });
  return async () => (await Promise.all(bodies)).join("\n");
}

/** What a test made, for `finish`: the session, the Home notes written, an agent's key. */
type Run = { page?: Page; session?: Api; written: string[]; agent?: Mcp; published?: string[] };

/**
 * A fresh session of Alice's on the note Hive inspection (written first, with Brood frames, in the file's vault).
 * Called inside the test's `try`, so `finish` cleans up whatever of it was done.
 */
async function start(freshSession: (user: "alice") => Promise<{ context: BrowserContext; page: Page }>, aliceApi: Api, n: Notes, run: Run) {
  await aliceApi.putNote(n.hive, texts.hive);
  await aliceApi.putNote(n.brood, texts.brood);
  const { context, page } = await freshSession("alice");
  run.page = page;
  run.session = await apiOf(context);
  await dismissInstallHint(page);
  await page.goto(noteUrl(n.hive));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  return { page, session: run.session };
}

/** Home shown through the vault menu (the first PIN entry), the test's Home notes written, every note embedded. */
async function showAndWrite(page: Page, session: Api, n: Notes, home: string[], folder: string, run: Run) {
  await revealDiscreet(page);
  await expect(page.getByRole("dialog", { name: "More vaults" })).toHaveCount(0);
  for (const path of home) {
    await session.putNote(path, path === n.swarm ? texts.swarm : harvestText(folder, n.hid));
    run.written.push(path);
  }
  await waitIndexed(session, [n.hive], firstIndex);
  await waitIndexed(session, [n.brood, ...home], nextIndex);
}

const cleanup = (description: string) => test.info().annotations.push({ type: "cleanup", description });

/**
 * For `finally`: the test's notes in the file's vault removed; the Home notes written, removed while shown (the second PIN entry, the call the PIN dialog makes),
 * each on its own, and their folder checked gone; then hidden, through the session's request context and through the
 * page, so the session ends hidden even when the page is gone. Nothing here throws: a failure is a "cleanup"
 * annotation, and the test's own failure stays the one reported.
 */
async function finish(run: Run, aliceApi: Api, n: Notes, folder: string) {
  const { session, written } = run;
  // The file's vault is shared by its tests: each takes its notes out again, or the next one's exact lists see them.
  for (const path of [n.hive, n.brood]) await aliceApi.deleteNote(path).catch((e: Error) => cleanup(`note not removed: ${e.message}`));
  if (session && written.length > 0) {
    await reservePin();
    const shown = await session.send("POST", "/api/account/discreet/reveal", { data: { pin: secrets().pin, minutes: null } }).catch(() => undefined);
    if (shown?.status() !== 200) cleanup(`Home not shown again to remove the test's notes: ${shown?.status()}`);
    for (const path of run.published ?? []) {
      const gone = await session.send("DELETE", `/api/publications/${encodePath(path)}`).catch(() => undefined);
      if (gone?.ok() !== true) cleanup(`Home note not unpublished: ${gone?.status()}`);
    }
    for (const path of written) await session.deleteNote(path).catch((e: Error) => cleanup(`Home note not removed: ${e.message}`));
    const left = await session.console("ls /Home").catch(() => undefined);
    if (left?.text.split("\n").some((line) => line.replace(/\/$/, "").trim() === folder)) cleanup(`/Home/${folder}/ survives as an empty folder`);
  }
  await run.agent?.dispose().catch((e: Error) => cleanup(`agent key not revoked: ${e.message}`));
  if (session) {
    const hidden = await session.send("POST", "/api/account/discreet/hide").catch(() => undefined);
    if (hidden?.ok() !== true) cleanup(`the session's hide answered ${hidden?.status()}`);
  }
  if (run.page) await endHidden(run.page);
  await session?.dispose();
}

/** The note paths `similar`'s or `semsearch`'s answer lists, in its order. */
const hitsOf = (text: string) => [...text.matchAll(/^\d+\. (\/[^#\n]+?\.md)(?:#[^\n]*?)?  lines /gm)].map((m) => m[1]);

test("discreet-0-7-0 similar: hidden, the console's similar over every vault and semsearch name nothing of Home; shown, the Home note is a hit; an agent's key granted Home reads it @synthesis @search @serial @discreet", async ({ freshSession, aliceApi, folder }, testInfo) => {
  test.setTimeout(480_000);
  const n = notesOf(folder);
  const run: Run = { written: [] };
  try {
    const { page, session } = await start(freshSession, aliceApi, n, run);
    await showAndWrite(page, session, n, [n.swarm], folder, run);
    const ask = `similar "${n.hive}" -n 50`;
    const topic = `semsearch "beekeeper hive queen swarm honey supers brood frames" -n 50`;

    // Shown: the session's credential opens Home like any other vault, so the default scope takes it in.
    const shownSimilar = await session.console(ask);
    expect(shownSimilar.isError, shownSimilar.text.split("\n")[0]).toBe(false);
    expect(hitsOf(shownSimilar.text), "shown: the neighbour and the Home note are hits").toEqual(expect.arrayContaining([n.brood, n.swarm]));
    expect(hitsOf((await session.console(topic)).text), "shown: semsearch finds the Home note").toContain(n.swarm);
    // An agent's key granted Home, made while Home is shown (a key on another vault too stays listed, and revocable,
    // once hidden).
    run.agent = await Mcp.create(session, `e2e discreet ${n.id}`, { [vault]: "read", Home: "read" });

    await hideDiscreet(page);

    // Hidden: the session's console, in its answers' text, names nothing of Home.
    const hidden = await session.console(ask);
    expect(hidden.isError, hidden.text.split("\n")[0]).toBe(false);
    expect(hitsOf(hidden.text), "hidden: the neighbour is still a hit").toContain(n.brood);
    expect(homeLines(hidden.text, n), "hidden: similar over every vault names nothing of Home").toEqual([]);
    const words = await session.console(topic);
    expect(hitsOf(words.text), "hidden: semsearch still finds the other vault's notes").toEqual(expect.arrayContaining([n.hive, n.brood]));
    expect(homeLines(words.text, n), "hidden: semsearch names nothing of Home").toEqual([]);
    // The Home note itself answers as a note of a vault that doesn't exist.
    const made = `Nowhere${n.id}`;
    const ofHomeNote = await session.console(`similar "${n.swarm}"`);
    const ofNowhere = await session.console(`similar "${n.swarm.replace("/Home/", `/${made}/`)}"`);
    expect(ofNowhere.isError, "a made-up vault's note").toBe(true);
    expect(ofHomeNote, "hidden: similar on the Home note").toEqual({ text: ofNowhere.text.replaceAll(made, "Home"), isError: true });

    // The console's page, the session's view on the screen.
    const got = answersOf(page);
    await page.goto("/console");
    const output = page.getByRole("log", { name: "Console output" });
    const command = page.getByRole("textbox", { name: "Command" });
    await expect(command).toBeEnabled();
    // Five hits, so the screen shows them whole: shown, Swarm would rank second, after Brood frames.
    await command.fill(`similar "${n.hive}" -n 5`);
    await command.press("Enter");
    await expect(output).toContainText(`similar to ${n.hive}, below every vault:`);
    await expect(output).toHaveAttribute("aria-busy", "false");
    await expect(output).toContainText(titleOf(n.brood));
    expect(homeLines(await output.innerText(), n), "hidden: the console's page shows nothing of Home").toEqual([]);
    expect(homeLines(await got(), n), "hidden: nothing of Home in the console's answers").toEqual([]);
    await shot(page, testInfo, "discreet");

    // The agent's key: Home granted, so read whatever the session shows (discreet vaults design).
    const byKey = await run.agent.sh(ask);
    expect(hitsOf(byKey.text), "an agent's key granted Home reads it while the session hides it").toContain(n.swarm);
  } finally {
    await finish(run, aliceApi, n, folder);
  }
});

test("discreet-0-7-0 similar notes: hidden, /api/similar on a Home note answers as for a made-up vault, and another vault's note's Links tab shows nothing of Home @similar @search @serial @discreet", async ({ freshSession, aliceApi, folder }) => {
  test.setTimeout(480_000);
  const n = notesOf(folder);
  const run: Run = { written: [] };
  try {
    const { page, session } = await start(freshSession, aliceApi, n, run);
    await showAndWrite(page, session, n, [n.swarm], folder, run);
    // Shown, the Home note's suggestions are answered (its vault has none to give: Swarm is Home's only note here).
    expect((await similarNotes(session, n.swarm)).state, "shown: the Home note's suggestions are answered").toBe("ready");
    await hideDiscreet(page);

    const made = `Nowhere${n.id}`;
    await expectAsMissing(session, (v) => `/api/similar/${v}/${encodeURIComponent(folder)}/${encodeURIComponent(titleOf(n.swarm))}.md`, made);

    // The note's Links tab: its neighbour, and nothing of Home.
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
    await expect(section, "hidden: the Links tab shows nothing of Home").not.toContainText(ofHome(n));
    expect(await got(), "hidden: nothing of Home in the page's answers").not.toMatch(ofHome(n));
  } finally {
    await finish(run, aliceApi, n, folder);
  }
});

test("discreet-0-7-0 graph: hidden, /api/graph/similarity and /api/graph/local on a Home note answer as for a made-up vault, and another vault's note's graph with its suggestions shows nothing of Home @graph @search @serial @discreet", async ({ freshSession, aliceApi, folder }) => {
  test.setTimeout(480_000);
  const n = notesOf(folder);
  const run: Run = { written: [] };
  try {
    const { page, session } = await start(freshSession, aliceApi, n, run);
    await showAndWrite(page, session, n, [n.swarm, n.harvest], folder, run);
    const local = (v: string) => `/api/graph/local?path=${encodeURIComponent(n.harvest.replace("/Home/", `/${v}/`))}`;
    const similarity = (v: string) => `/api/graph/similarity?path=${encodeURIComponent(n.harvest.replace("/Home/", `/${v}/`))}`;
    // Shown: Home's graph is drawn and its edge measured.
    expect((await answerOf(session, local("Home"))).status, "shown: Home's local graph").toBe(200);
    const measured = await session.json<{ state: string; pairs: { a: string; b: string; score: number | null }[] }>("GET", similarity("Home"));
    expect(measured.state, "shown: Home's edges measured").toBe("ready");
    expect(measured.pairs.length, "shown: Honey harvest's link to Swarm").toBe(1);
    await hideDiscreet(page);

    const made = `Nowhere${n.id}`;
    await expectAsMissing(session, similarity, made);
    await expectAsMissing(session, local, made);

    // The note's graph, its Suggestions layer on: its neighbour, nothing of Home.
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
    await expect(suggested).toHaveCount(1);
    await expect(suggested).toHaveAccessibleName(`Suggested link: ${titleOf(n.brood)}`);
    await expect(dialog, "hidden: the graph shows nothing of Home").not.toContainText(ofHome(n));
    const labels = await drawing.getByRole("button").evaluateAll((els) => els.map((e) => e.getAttribute("aria-label") ?? ""));
    expect(labels.filter((l) => ofHome(n).test(l)), "hidden: no node or suggestion of Home in the drawing").toEqual([]);
    expect(await got(), "hidden: nothing of Home in the page's answers").not.toMatch(ofHome(n));
  } finally {
    await finish(run, aliceApi, n, folder);
  }
});

type Publication = { path: string; older: boolean };

/** The window's count line for a list (PublishedPage.tsx), or null where it shows none: no copy older. */
function countLine(list: Publication[]) {
  const older = list.filter((p) => p.older).length;
  if (older === 0) return null;
  return `${older} of ${list.length} ${list.length === 1 ? "copy is" : older === 1 ? "copies is" : "copies are"} older than ${older === 1 ? "its note" : "their notes"}.`;
}

/** The window as the server's list has it: the count line (or none), the rows' paths in the list's order. */
async function expectWindowOf(page: Page, list: Publication[], what: string) {
  const window = page.getByRole("dialog", { name: "Published", exact: true });
  const line = window.getByText(/ older than (its|their) notes?\.$/);
  const expected = countLine(list);
  if (expected) await expect(line, `${what}: the count line`).toHaveText(expected);
  else await expect(line, `${what}: no count line`).toHaveCount(0);
  if (list.length === 0) await expect(window.getByText("No published notes.")).toBeVisible();
  else await expect(window.getByRole("list", { name: "Published notes" }).getByRole("link"), `${what}: the rows`).toHaveText(list.map((p) => p.path));
}

test("discreet-0-7-0 published: hidden, the Published window has no row, count or word of Home, as GET /api/publications through the session has it @overview @serial @discreet", async ({ freshSession, aliceApi, folder }) => {
  test.setTimeout(300_000);
  const n = notesOf(folder);
  const lantern = `/Home/${folder}/Lantern ${n.hid}.md`;
  const run: Run = { written: [], published: [] };
  try {
    const { page, session } = await start(freshSession, aliceApi, n, run);
    await revealDiscreet(page);
    await expect(page.getByRole("dialog", { name: "More vaults" })).toHaveCount(0);
    await session.putNote(lantern, "# Lantern\n\nThe lantern on the porch.\n");
    run.written.push(lantern);
    // Published as the dialog does (the fresh sign-in opened the password window); the link stays in this answer.
    const made = await session.send("POST", `/api/publications/${encodePath(lantern)}`, { data: { depth: 0 } });
    // Withdrawn at the end whatever the answer said, so no Home copy outlives the test.
    if (made.ok()) run.published!.push(lantern);
    expect(made.status(), "the Home note published").toBe(201);
    // Changed after, so its copy is older and counts in the count line too.
    await session.putNote(lantern, "# Lantern\n\nThe lantern on the porch, lit.\n");

    // Shown: a row of its own, and the window is the server's list.
    const shown = await session.json<Publication[]>("GET", "/api/publications");
    expect(shown.find((p) => p.path === lantern), "shown: the Home note's copy, older than its note").toMatchObject({ older: true });
    await page.goto("/published");
    await expectWindowOf(page, shown, "shown");

    // Hidden, as ✕ does (the pill is under the window); the window opened again.
    const hiding = await session.send("POST", "/api/account/discreet/hide");
    expect(hiding.ok(), "hidden").toBe(true);
    const got = answersOf(page);
    await page.goto("/published");
    const hidden = await session.json<Publication[]>("GET", "/api/publications");
    expect(hidden.map((p) => p.path), "hidden: GET /api/publications is the shown list without Home's copy").toEqual(shown.map((p) => p.path).filter((p) => p !== lantern));
    await expectWindowOf(page, hidden, "hidden");
    const window = page.getByRole("dialog", { name: "Published", exact: true });
    await expect(window, "hidden: nothing of Home in the window").not.toContainText(new RegExp(`/Home/|\\bHome\\b|Lantern|${n.hid}`));
    expect(JSON.stringify(hidden), "hidden: nothing of Home in GET /api/publications").not.toMatch(new RegExp(`/Home/|Lantern|${n.hid}`));
    expect(await got(), "hidden: nothing of Home in the page's answers").not.toMatch(new RegExp(`/Home/|Lantern|${n.hid}`));
  } finally {
    await finish(run, aliceApi, n, folder);
  }
});
