// SPDX-License-Identifier: AGPL-3.0-or-later
// What the calendar's tests share: an agent with other rights, the dates as the app writes them, the way to the vault menu
// on either viewport, and the clean-up of a test's folder.
import { request, type BrowserContext, type Locator, type Page, type TestInfo } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, Mcp } from "../support/api";
import { baseURL } from "../support/env";
import { test as base, expect, onPhone } from "../support/test";
import { addDays, isoWeek, monday, type Day } from "../support/time";

/**
 * An agent with rights the shared `mcp` (write on Work) hasn't: maintain for `mkcp`, or a discreet vault. Made through
 * `session` (a browser context of a session of its own) where the vault is one only that session shows, a discreet
 * vault revealed. `dispose` revokes its key, through the shared session, which outlives the test's own.
 */
export async function makeAgent(vaults: Record<string, "read" | "write" | "maintain">, label: string, session?: BrowserContext) {
  const owner = await Api.as("alice");
  const maker = session
    ? await Api.from(await request.newContext({ baseURL, storageState: await session.storageState(), extraHTTPHeaders: { Origin: baseURL } }), "alice")
    : owner;
  // The password is checked at most 5 times a minute per account, across every tag's tests: it is confirmed only when
  // the session's password window isn't open already (a fresh sign-in opens it).
  const data = { name: `e2e calendar ${label} ${randomBytes(3).toString("hex")}`, vaults, expiresInDays: 30 };
  let answer = await maker.context.post("/api/keys", { data });
  if (answer.status() === 401) {
    await maker.confirmPassword();
    answer = await maker.context.post("/api/keys", { data });
  }
  expect(answer.status(), `POST /api/keys: ${answer.status()}`).toBe(201);
  const created = (await answer.json()) as { key: string; apiKey: { id: string } };
  const mcp = new Mcp(await request.newContext({ baseURL }), created.key, { api: owner, id: created.apiKey.id });
  return {
    mcp,
    dispose: async () => {
      await mcp.dispose();
      if (maker !== owner) await maker.dispose();
      await owner.dispose();
    },
  };
}

export const test = base;

export { expect, onPhone };

// The app's own words for dates (web/src/calendar/dates.ts), not Intl's: en-GB says "Sept" where the app says "Sep".
const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
const WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const parts = (day: Day) => {
  const [year, month, date] = day.split("-").map(Number);
  return { year, month, date, weekday: new Date(Date.UTC(year, month - 1, date)).getUTCDay() };
};

/** "Thu 12", with `month` "Thu 12 Nov". */
export const shortDay = (day: Day, month = false) => {
  const p = parts(day);
  return `${WEEKDAYS[p.weekday].slice(0, 3)} ${p.date}${month ? ` ${MONTHS[p.month - 1].slice(0, 3)}` : ""}`;
};
/** "Thursday, 12 November". */
export const dayHeading = (day: Day) => {
  const p = parts(day);
  return `${WEEKDAYS[p.weekday]}, ${p.date} ${MONTHS[p.month - 1]}`;
};
/** "Thursday, 12 November 2026": a new day note's heading. */
export const longDay = (day: Day) => `${dayHeading(day)} ${parts(day).year}`;
/** "16 Dec", with `long` "16 December". */
export const dayAndMonth = (day: Day, long = false) => {
  const p = parts(day);
  return `${p.date} ${long ? MONTHS[p.month - 1] : MONTHS[p.month - 1].slice(0, 3)}`;
};
/** "November 2026". */
export const monthTitle = (day: Day) => `${MONTHS[parts(day).month - 1]} ${parts(day).year}`;
/** "2026-W46". */
export const weekId = (day: Day) => {
  const w = isoWeek(day);
  return `${w.year}-W${String(w.week).padStart(2, "0")}`;
};
/** "Week 46 · 9–15 November 2026", across months and years as the app writes it. */
export function weekTitle(day: Day) {
  const first = parts(monday(day));
  const last = parts(addDays(monday(day), 6));
  const range =
    first.year !== last.year
      ? `${first.date} ${MONTHS[first.month - 1]} ${first.year} – ${last.date} ${MONTHS[last.month - 1]} ${last.year}`
      : first.month !== last.month
        ? `${first.date} ${MONTHS[first.month - 1]} – ${last.date} ${MONTHS[last.month - 1]} ${last.year}`
        : `${first.date}–${last.date} ${MONTHS[last.month - 1]} ${last.year}`;
  return `Week ${isoWeek(day).week} · ${range}`;
}
/** "Week 46 · 9–15 Nov", a month's week heading. */
export function shortWeekTitle(day: Day) {
  const first = parts(monday(day));
  const last = parts(addDays(monday(day), 6));
  const short = (m: number) => MONTHS[m - 1].slice(0, 3);
  const range = first.month === last.month ? `${first.date}–${last.date} ${short(last.month)}` : `${first.date} ${short(first.month)} – ${last.date} ${short(last.month)}`;
  return `Week ${isoWeek(day).week} · ${range}`;
}
/** The span of an entry over several days: "Fri 13 – Wed 18", across months "Fri 27 Nov – Wed 2 Dec". */
export const rangeText = (first: Day, last: Day) => {
  const months = parts(first).month !== parts(last).month;
  return `${shortDay(first, months)} – ${shortDay(last, months)}`;
};

/** The address of a note in the web app. */
export const notePage = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

/** A tag of the test's own (letters and digits), for the calendar's tag filter: other tests' entries stay out. */
export const tagOf = (folder: string) => `t${folder.replace(/[^a-z0-9]/g, "")}`;

/** The week view of Work at a given week, named in the address so that the remembered view (week or month) can't change it. */
export const weekUrl = (vault: string, day: Day, tag?: string) => `/calendar/${vault}?week=${weekId(day)}${tag ? `&tag=${tag}` : ""}`;

/** On the phone, the install hint may sit over controls: dismissed with Not now, as a person would, whenever it shows. */
export async function dismissInstallHint(page: Page) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
}

/** The tree's panel: the sidebar on a desktop, the drawer (opened here) on a phone. */
export async function treePanel(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (onPhone(testInfo)) {
    await page.getByRole("button", { name: "Open file tree" }).click();
    const drawer = page.getByRole("dialog", { name: "File tree" });
    await expect(drawer).toBeVisible();
    return drawer;
  }
  const sidebar = page.getByRole("complementary", { name: "File tree" });
  await expect(sidebar).toBeVisible();
  return sidebar;
}

/** Makes `vault` the active one through the switcher. */
export async function switchVault(page: Page, panel: Locator, vault: string) {
  await panel.getByRole("button", { name: /^Switch vault/ }).click();
  await page.getByRole("dialog").getByRole("button", { name: new RegExp(`^${vault}\\b`) }).click();
  await expect(panel.getByRole("heading", { level: 2, name: vault })).toBeVisible();
}

/** Opens the note's ⋯ (Note actions) and returns its menu. */
export async function noteActions(page: Page) {
  await page.getByRole("button", { name: "Note actions" }).click();
  const menu = page.getByRole("menu", { name: "Note actions" });
  await expect(menu).toBeVisible();
  return menu;
}

/** The note's calendar line in its header. */
export const entryLine = (page: Page) => page.getByRole("group", { name: "In the calendar" });

/** Every note below `/Work/<folder>/` goes to the trash: nothing of the test stays in What's on or the badge. */
export async function cleanFolder(api: Api, folder: string) {
  const listed = await api.console(`rg --files "/Work/${folder}"`);
  if (listed.isError) return;
  for (const path of listed.text.split("\n").map((l) => l.trim()).filter((l) => l.startsWith("/Work/"))) {
    await api.deleteNote(path).catch(() => {});
  }
}

/** The colour `var(--warning)` resolves to on the page, as `getComputedStyle` gives a colour. */
export const warningColour = (page: Page) =>
  page.evaluate(() => {
    const probe = document.createElement("span");
    probe.style.color = "var(--warning)";
    document.body.append(probe);
    const colour = getComputedStyle(probe).color;
    probe.remove();
    return colour;
  });

/** A frontmatter block and a body, as a note's text. */
export const note = (front: Record<string, string>, body: string) =>
  `---\n${Object.entries(front)
    .map(([k, v]) => `${k}: ${v}`)
    .join("\n")}\n---\n${body}`;
