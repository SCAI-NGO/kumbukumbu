// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { userKey, type SealedMirror } from "eidolon-core/store/mirror";
import { useOfflineCopy } from "../../offline/copy";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../../test/app";

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }, { name: "Diary", notes: 1 }]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    "GET /api/account/calendar": () => json(200, { zone: "Europe/Vienna", serverZone: "Europe/Vienna" }),
    ...overrides,
  });
}

function mockSystem(dark: boolean) {
  vi.stubGlobal("matchMedia", (q: string) => ({ matches: dark && q.includes("dark"), addEventListener() {}, removeEventListener() {} }));
}

beforeEach(() => {
  localStorage.clear();
  delete document.documentElement.dataset.scheme;
});

afterEach(() => {
  cleanup();
  useOfflineCopy.getState().clear();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("Settings: Your data", () => {
  it("has a section Your data, with the export of one's vaults, then Delete account", async () => {
    mockSystem(true);
    api();
    renderApp("/settings/data");
    const yourData = await screen.findByRole("region", { name: "Your data" });
    expect(within(yourData).getByRole("button", { name: "Export my vaults…" })).toBeInTheDocument();
    expect(yourData).toHaveTextContent(/each folder opens in Obsidian as a vault \(no history, trash or checkpoints\)/);
    const titles = within(screen.getByRole("dialog", { name: "Settings" })).getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(titles.slice(1)).toEqual(["Your data", "Delete account"]);
  });

  it("deletes the account with the password and the username typed, and goes to sign-in", async () => {
    const key = `kumbu.lastNote.${await userKey(user.id)}`;
    localStorage.setItem(key, "/Work/inbox.md");
    const bodies: unknown[] = [];
    // As the server: once the account is gone, its session is too.
    let deleted = false;
    api({
      "POST /api/account/delete": async (r) => (bodies.push(await r.json()), (deleted = true), noContent()),
      "GET /api/session": () => (deleted ? json(401, { error: "Sign in first." }) : json(200, { user, previousSignInAt: null })),
    });
    const router = renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    const button = within(form).getByRole("button", { name: "Delete my account" });
    await userEvent.type(within(form).getByLabelText("Current password"), "my password, long");
    expect(button).toBeDisabled();
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(button);

    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(bodies).toEqual([{ password: "my password, long", confirm: "ada" }]);
    // Nothing of the account stays on the device: its last note goes with it.
    expect(localStorage.getItem(key)).toBeNull();
    expect(await screen.findByRole("status")).toHaveTextContent("Your account is deleted");
  });

  it("deleting the account removes the offline copy from this device", async () => {
    const mirror = { header: { copy: "c1", slot: { id: "s1", token: "tok-ada" } }, locked: false, lock: vi.fn(), lockEverywhere: vi.fn() };
    useOfflineCopy.getState().set(mirror.header as never, mirror as unknown as SealedMirror);
    api({ "POST /api/account/delete": () => noContent() });
    const router = renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    await userEvent.type(within(form).getByLabelText("Current password"), "my password, long");
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(within(form).getByRole("button", { name: "Delete my account" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(mirror.lock).toHaveBeenCalled();
    // Gone, not only locked: no header of it is left in the tab.
    expect(useOfflineCopy.getState()).toMatchObject({ header: null, mirror: null });
  });

  it("lists her shared vaults and sends the choice", async () => {
    const bodies: unknown[] = [];
    api({
      "GET /api/vaults": () =>
        json(200, [
          { name: "Gartenarbeit", notes: 3, shared: true, members: 2 },
          { name: "Work", notes: 1, shared: true, members: 0 },
          { name: "Diary", notes: 1 },
          { name: "Bobs", notes: 4, owner: "Bob", ownerUsername: "bob", shared: true, level: "write" },
        ]),
      "POST /api/account/delete": async (r) => (bodies.push(await r.json()), noContent()),
    });
    renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    const group = await within(form).findByRole("group", { name: "Your shared vaults" });
    // Only her own vaults with an active member; not one shared with nobody, nor one that is somebody else's.
    expect(group).toHaveTextContent("Gartenarbeit");
    expect(group).not.toHaveTextContent("Work");
    expect(group).not.toHaveTextContent("Bobs");
    const give = within(group).getByRole("radio", { name: /Give members 7 days/ });
    const now = within(group).getByRole("radio", { name: /Delete it for everyone now/ });
    expect(give).toBeChecked();
    // One vault: the choices speak of it, not of each.
    expect(give.closest("label")).toHaveTextContent("Give members 7 days: it stays read-only for its members for 7 days");
    expect(now.closest("label")).toHaveTextContent("Delete it for everyone now: it is deleted at once, and each member is told");

    await userEvent.click(now);
    await userEvent.type(within(form).getByLabelText("Current password"), "my password, long");
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(within(form).getByRole("button", { name: "Delete my account" }));
    await waitFor(() => expect(bodies).toEqual([{ password: "my password, long", confirm: "ada", sharedVaults: "delete-now" }]));
  });

  it("sends the default of 7 days unless she chooses otherwise", async () => {
    const bodies: unknown[] = [];
    api({
      "GET /api/vaults": () => json(200, [{ name: "Gartenarbeit", notes: 3, shared: true, members: 1 }]),
      "POST /api/account/delete": async (r) => (bodies.push(await r.json()), noContent()),
    });
    renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    await within(form).findByRole("group", { name: "Your shared vaults" });
    await userEvent.type(within(form).getByLabelText("Current password"), "my password, long");
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(within(form).getByRole("button", { name: "Delete my account" }));
    await waitFor(() => expect(bodies).toEqual([{ password: "my password, long", confirm: "ada", sharedVaults: "give-members-7-days" }]));
  });

  it("offers no choice without shared vaults", async () => {
    const bodies: unknown[] = [];
    api({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3, shared: true, members: 0 }, { name: "Diary", notes: 1 }]),
      "POST /api/account/delete": async (r) => (bodies.push(await r.json()), noContent()),
    });
    renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    await userEvent.type(within(form).getByLabelText("Current password"), "my password, long");
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    expect(within(form).queryByRole("radio")).not.toBeInTheDocument();
    await userEvent.click(within(form).getByRole("button", { name: "Delete my account" }));
    await waitFor(() => expect(bodies).toEqual([{ password: "my password, long", confirm: "ada" }]));
  });

  it("shows the server's sentence when the account deletion is refused", async () => {
    api({ "POST /api/account/delete": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    await userEvent.type(within(form).getByLabelText("Current password"), "wrong password, long");
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(within(form).getByRole("button", { name: "Delete my account" }));
    expect(await within(form).findByText("The password is wrong.")).toBeInTheDocument();
  });

  it("offers the primary admin no account deletion", async () => {
    api({ "GET /api/session": () => json(200, { user: { ...user, role: "primary_admin" }, previousSignInAt: null }) });
    renderApp("/settings/data");
    const form = await screen.findByRole("region", { name: "Delete account" });
    expect(form).toHaveTextContent("the server always needs one");
    expect(within(form).queryByRole("button")).not.toBeInTheDocument();
  });
});
