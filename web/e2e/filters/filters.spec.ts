// SPDX-License-Identifier: AGPL-3.0-or-later
// Filters for Users, Invitations and Access (spec 2026-11-18-list-filters-design.md, PR #236). The lists are
// global (the server's users and invitations, Work's members), so the tests are @serial: they read the counts before,
// make entries of their own with a random name, assert the difference and the text filter on those names, and remove
// what they made in afterEach (registered as they are made, so a timeout skips no clean-up). An admin invitation can
// only be revoked, never deleted: each pass of "filters #1" leaves three revoked invitations behind in the list.
//
// Promises in scope, and the test that covers each:
// - a list opens showing everything: All chosen, no filter text, every entry → "filters #1", "#2", "#3"
// - the text filter matches the name anywhere, ignoring case, trimmed → "filters #1" (note), "#2" (display name and
//   username), "#3" (label)
// - one status at a time or All, each with its count; a status at 0 stays visible → "filters #1", "#2", "#3"
// - counts follow the typed text → "filters #1", "#3"
// - "Showing n of m" and Show all, which clears both filters → "filters #1", "#2", "#3"
// - nothing matches: "No invitations match." / "No users match." / "No members match." → "filters #1", "#2", "#3"
// - the filter stays put across what people do in the view (revoking an invitation, withdrawing a member's
//   invitation) → "filters #1", "#3"
// - the statuses in the spec's order: Invitations Open, Used, Expired, Revoked; Users Active, Deactivated; Access
//   Invited, To confirm, Member → "filters #1", "#2", "#3"
// - the status choice is a radio group: one tab stop, the arrow keys move within it and the list follows → "filters #1"
// Left out: users of the test's own (an account is made only by signing up through an invitation, with a recovery
// key, and a deleted one leaves a receipt): the Users test filters Alice and Bob and checks the counts against the
// API's list; Used, Expired and To confirm entries of the test's own (redeeming takes another account, expiring a
// day); the filter surviving a refetch while the view stays open (Vitest); the filter not kept once the view closes
// is seen on reopening Access ("filters #3"); the bar hidden on an empty list (none of the three lists is ever empty
// on the stack: Alice and Bob, the setup's invitations, Bob in Work; Vitest covers it).
import type { Locator, Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import type { Api } from "../support/api";
import { secrets } from "../support/env";
import { reservePassword } from "../support/session";
import { expect, shot, test } from "../support/test";
import { notePage, treePanel } from "../calendar/helpers";

type Invitation = { id: string; note?: string | null; state: string };
type User = { id: string; display: string; username: string; active: boolean };
type Member = { id: string; label: string; status: string; redeemedAt?: string | null };

/** What a test made, removed in afterEach: a `finally` doesn't run once the test's time is up, a hook does. */
let made: { invitations: string[]; memberships: string[]; notes: string[] } = { invitations: [], memberships: [], notes: [] };

// On the phone the install hint can sit over the lists: answered beforehand, as Not now leaves it.
test.beforeEach(async ({ page }) => {
  made = { invitations: [], memberships: [], notes: [] };
  await page.addInitScript(() => localStorage.setItem("kumbu.installHint", "dismissed"));
});

// Revoked or withdrawn already (by the test's own steps): 404; anything else is a clean-up that failed.
test.afterEach(async ({ aliceApi }) => {
  for (const id of made.invitations)
    expect([204, 404], `revoke invitation ${id}`).toContain((await aliceApi.send("DELETE", `/api/admin/invitations/${id}`)).status());
  for (const id of made.memberships)
    expect([204, 404], `withdraw membership ${id}`).toContain((await aliceApi.send("DELETE", `/api/vaults/Work/memberships/${id}`)).status());
  for (const path of made.notes) await aliceApi.deleteNote(path);
});

/** The status choice: its radios' labels in order, e.g. ["All 5", "Open 2", …]. */
const statusRow = (scope: Page | Locator) => scope.getByRole("group", { name: "Status" });
const choices = (scope: Page | Locator) =>
  statusRow(scope)
    .getByRole("radio")
    .evaluateAll((radios) => radios.map((r) => (r as HTMLInputElement).labels![0].textContent!.trim()));
const radio = (scope: Page | Locator, label: string) => statusRow(scope).getByRole("radio", { name: new RegExp(`^${label} \\d+$`) });
/** A status chosen as a person does: a tap on its pill (the radio itself is visually hidden inside it). */
const pick = (scope: Page | Locator, label: string) =>
  statusRow(scope)
    .getByText(new RegExp(`^${label} \\d+$`))
    .click();

/** The counts per status of `items`, in the row's words and order. */
function expected<T>(items: T[], statuses: [string, string][], status: (item: T) => string) {
  return [`All ${items.length}`, ...statuses.map(([value, label]) => `${label} ${items.filter((i) => status(i) === value).length}`)];
}

const has = (text: string, needle: string) => text.toLocaleLowerCase().includes(needle.trim().toLocaleLowerCase());

/** The status line: "Showing n of m" with Show all, or nothing while everything shows. */
async function showing(scope: Page | Locator, line: string | null) {
  if (line === null) {
    await expect(scope.getByText(/^Showing \d+ of \d+$/)).toHaveCount(0);
    await expect(scope.getByRole("button", { name: "Show all" })).toHaveCount(0);
  } else {
    await expect(scope.getByText(line, { exact: true })).toBeVisible();
    await expect(scope.getByRole("button", { name: "Show all" })).toBeVisible();
  }
}

const invitationStatuses: [string, string][] = [
  ["open", "Open"],
  ["used", "Used"],
  ["expired", "Expired"],
  ["revoked", "Revoked"],
];

/** A user invitation with a note, through the API the dialog calls; its link and code are never read. */
async function invite(api: Api, note: string) {
  const body = { role: "user", note, expiresInDays: 1 };
  let response = await api.send("POST", "/api/admin/invitations", { data: body });
  if (response.status() === 401 || response.status() === 403) {
    await api.confirmPassword();
    response = await api.send("POST", "/api/admin/invitations", { data: body });
  }
  expect(response.status(), "POST /api/admin/invitations").toBe(201);
  return ((await response.json()) as { invitation: { id: string } }).invitation.id;
}

test("filters #1: Invitations filter by note and status, with counts that follow the text, and keep the filter across a revoke @filters @serial @phone", async ({ page, aliceApi }, testInfo) => {
  const mark = randomBytes(3).toString("hex");
  const notes = [`Filter ${mark} alpha`, `FILTER ${mark} beta`, `filter ${mark} gamma`];
  const items = page.getByRole("listitem", { name: /^Invitation for / });
  const search = page.getByRole("searchbox", { name: "Filter by note" });
  const before = await aliceApi.json<Invitation[]>("GET", "/api/admin/invitations");
  await page.goto("/admin/invitations");
  // It opens showing everything.
  await expect.poll(() => choices(page)).toEqual(expected(before, invitationStatuses, (i) => i.state));
  await expect(radio(page, "All")).toBeChecked();
  await expect(search).toHaveValue("");
  await expect(items).toHaveCount(before.length);
  await showing(page, null);

  for (const note of notes) made.invitations.push(await invite(aliceApi, note));
  await aliceApi.json("DELETE", `/api/admin/invitations/${made.invitations[2]}`);
  const after = await aliceApi.json<Invitation[]>("GET", "/api/admin/invitations");
  await page.reload();
  const counts = expected(after, invitationStatuses, (i) => i.state);
  await expect.poll(() => choices(page)).toEqual(counts);
  // The difference is ours: two open, one revoked.
  const number = (row: string[], i: number) => Number(row[i].split(" ").pop());
  const was = expected(before, invitationStatuses, (i) => i.state);
  expect([number(counts, 0) - number(was, 0), number(counts, 1) - number(was, 1), number(counts, 4) - number(was, 4)]).toEqual([3, 2, 1]);

  // The note anywhere, ignoring case, the text trimmed; the counts follow it.
  await search.fill(`  fIlTeR ${mark.toUpperCase()} `);
  await expect.poll(() => choices(page)).toEqual(["All 3", "Open 2", "Used 0", "Expired 0", "Revoked 1"]);
  await expect(items).toHaveCount(3);
  for (const note of notes) await expect(items.filter({ hasText: note })).toHaveCount(1);
  await showing(page, `Showing 3 of ${after.length}`);
  await shot(page, testInfo, "filters");

  // A radio group: from All, the arrow key moves to Open and chooses it.
  await radio(page, "All").focus();
  await page.keyboard.press("ArrowRight");
  await expect(radio(page, "Open")).toBeChecked();
  await expect(radio(page, "Open")).toBeFocused();
  await expect(items).toHaveCount(2);
  await expect(items.filter({ hasText: notes[2] })).toHaveCount(0);
  await showing(page, `Showing 2 of ${after.length}`);

  // Revoking one keeps the filter as it is.
  await items
    .filter({ hasText: notes[0] })
    .getByRole("button", { name: /^Revoke invitation/ })
    .click();
  const dialog = page.getByRole("alertdialog", { name: "Revoke this invitation?" });
  await dialog.getByRole("button", { name: "Revoke" }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(() => choices(page)).toEqual(["All 3", "Open 1", "Used 0", "Expired 0", "Revoked 2"]);
  await expect(search).toHaveValue(`  fIlTeR ${mark.toUpperCase()} `);
  await expect(radio(page, "Open")).toBeChecked();
  await expect(items).toHaveCount(1);
  await expect(items).toContainText(notes[1]);

  // Nothing matches: said in words; a status at 0 stays.
  await search.fill(`filter ${mark} none`);
  await expect(page.getByText("No invitations match.")).toBeVisible();
  await expect(items).toHaveCount(0);
  await expect.poll(() => choices(page)).toEqual(["All 0", "Open 0", "Used 0", "Expired 0", "Revoked 0"]);

  // Show all clears both.
  await page.getByRole("button", { name: "Show all" }).click();
  await expect(search).toHaveValue("");
  await expect(radio(page, "All")).toBeChecked();
  await expect(items).toHaveCount(after.length);
  await showing(page, null);
});

test("filters #2: Users filter by display name and username, ignoring case, and by Active or Deactivated @filters @serial", async ({ page, aliceApi }) => {
  const users = await aliceApi.json<User[]>("GET", "/api/admin/users");
  const statuses: [string, string][] = [
    ["active", "Active"],
    ["deactivated", "Deactivated"],
  ];
  const state = (u: User) => (u.active ? "active" : "deactivated");
  const search = page.getByRole("searchbox", { name: "Filter by name" });
  const row = (u: User) => page.getByRole("listitem", { name: u.display, exact: true });
  /** Every user's row shows exactly when `shown` says so. */
  const rows = async (shown: (u: User) => boolean) => {
    for (const u of users) await expect(row(u), `${u.username} ${shown(u) ? "shown" : "hidden"}`).toHaveCount(shown(u) ? 1 : 0);
  };

  await page.goto("/admin/users");
  await expect.poll(() => choices(page)).toEqual(expected(users, statuses, state));
  await expect(radio(page, "All")).toBeChecked();
  await rows(() => true);
  await showing(page, null);

  // The display name and the username, in any case.
  for (const typed of ["ALI", "bO", "lic"]) {
    const matching = users.filter((u) => has(`${u.display} ${u.username}`, typed));
    expect(matching.length, `someone matches ${typed}`).toBeGreaterThan(0);
    await search.fill(typed);
    await expect.poll(() => choices(page)).toEqual(expected(matching, statuses, state));
    await rows((u) => matching.includes(u));
    await showing(page, matching.length < users.length ? `Showing ${matching.length} of ${users.length}` : null);
  }

  await search.fill("");
  await pick(page, "Deactivated");
  const deactivated = users.filter((u) => !u.active);
  await rows((u) => !u.active);
  if (deactivated.length === 0) await expect(page.getByText("No users match.")).toBeVisible();
  await showing(page, `Showing ${deactivated.length} of ${users.length}`);
  await page.getByRole("button", { name: "Show all" }).click();
  await expect(radio(page, "All")).toBeChecked();
  await rows(() => true);
  await showing(page, null);
});

test("filters #3: Access filters Work's members by label and status, and keeps the filter across a withdrawal @filters @serial", async ({ page, aliceApi, folder }, testInfo) => {
  const mark = randomBytes(3).toString("hex");
  const labels = [`Filter ${mark} one`, `FILTER ${mark} two`];
  const statuses: [string, string][] = [
    ["invited", "Invited"],
    ["confirm", "To confirm"],
    ["member", "Member"],
  ];
  const state = (m: Member) => (m.status === "active" ? "member" : m.redeemedAt ? "confirm" : "invited");
  const members = async () => (await aliceApi.json<{ members: Member[] }>("GET", "/api/vaults/Work/access")).members;
  const note = `/Work/${folder}/Note.md`;
  await aliceApi.putNote(note, "# Note\n");
  made.notes.push(note);

  /** Work's ⋯ menu, Share…: the Access dialog. */
  const openAccess = async () => {
    const panel = await treePanel(page, testInfo);
    await panel.getByRole("button", { name: "Actions for Work" }).click();
    await page.getByRole("menuitem", { name: "Share…" }).click();
    const dialog = page.getByRole("dialog", { name: "Who has access to Work" });
    await expect(dialog).toBeVisible();
    return dialog;
  };
  const before = await members();
  await page.goto(notePage(note));
  let dialog = await openAccess();
  await expect.poll(() => choices(dialog)).toEqual(expected(before, statuses, state));
  await expect(radio(dialog, "All")).toBeChecked();
  await showing(dialog, null);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();

  // Two invitations of the test's own, as Share… makes them: each needs Alice's password.
  // The password window is checked first (Security.cs:184-188): on 401 it is opened, then the invitation sent again,
  // as share.ts does. Never the body in a message: the link holds the invitation's secret.
  for (const label of labels) {
    const send = async () => {
      await reservePassword("alice");
      return aliceApi.send("POST", "/api/vaults/Work/invitations", {
        data: { password: secrets().passwords.alice, label, role: "read", expiresInDays: 1, confirm: false },
      });
    };
    let response = await send();
    if (response.status() === 401) {
      await aliceApi.confirmPassword();
      response = await send();
    }
    expect(response.status(), "POST /api/vaults/Work/invitations").toBe(201);
    made.memberships.push(((await response.json()) as { id: string }).id);
  }
  const after = await members();
  dialog = await openAccess();
  const counts = expected(after, statuses, state);
  await expect.poll(() => choices(dialog)).toEqual(counts);
  expect(Number(counts[1].split(" ").pop()) - Number(expected(before, statuses, state)[1].split(" ").pop()), "two invited more").toBe(2);

  const search = dialog.getByRole("searchbox", { name: "Filter by name" });
  const list = dialog.getByRole("list", { name: "Members" }).getByRole("listitem");
  await search.fill(`filter ${mark.toUpperCase()}`);
  await expect.poll(() => choices(dialog)).toEqual(["All 2", "Invited 2", "To confirm 0", "Member 0"]);
  await expect(list).toHaveCount(2);
  for (const label of labels) await expect(list.filter({ hasText: label })).toHaveCount(1);
  await showing(dialog, `Showing 2 of ${after.length}`);

  // Withdrawing one keeps the filter.
  await list.filter({ hasText: labels[0] }).getByRole("button", { name: "Withdraw" }).click();
  await expect.poll(() => choices(dialog)).toEqual(["All 1", "Invited 1", "To confirm 0", "Member 0"]);
  await expect(search).toHaveValue(`filter ${mark.toUpperCase()}`);
  await expect(list).toHaveCount(1);
  await expect(list).toContainText(labels[1]);
  await showing(dialog, `Showing 1 of ${after.length - 1}`);

  // A status chosen narrows the text's matches; none left says so.
  await pick(dialog, "Member");
  await expect(dialog.getByText("No members match.")).toBeVisible();
  await expect(list).toHaveCount(0);
  await dialog.getByRole("button", { name: "Show all" }).click();
  await expect(search).toHaveValue("");
  await expect(radio(dialog, "All")).toBeChecked();
  await expect(list).toHaveCount(after.length - 1);

  // Closed and opened again, the filter starts afresh.
  await search.fill(mark);
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  dialog = await openAccess();
  await expect(dialog.getByRole("searchbox", { name: "Filter by name" })).toHaveValue("");
  await showing(dialog, null);
});
