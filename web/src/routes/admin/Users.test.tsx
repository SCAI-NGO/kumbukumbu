// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../../state/session";
import { fakeApi, json, noContent, renderApp } from "../../test/app";

const root = { id: "u0", username: "root", display: "Root", role: "primary_admin" };
const admin = { id: "u1", username: "ada", display: "Ada", role: "admin" };
const plain = { id: "u2", username: "bob", display: "Bob", role: "user" };

const row = (u: typeof root, extra = {}) => ({
  ...u,
  active: true,
  createdAt: "2026-10-01T09:00:00Z",
  lastSignInAt: "2026-10-20T09:00:00Z",
  vaults: 2,
  bytesStored: 1536,
  ...extra,
});

function api(who = admin, extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: who, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, []),
    "GET /api/admin/users": () =>
      json(200, [row(root), row(admin), row(plain, { lastSignInAt: null, bytesStored: 5 * 1024 * 1024 + 300000, vaults: 0 })]),
    "GET /api/admin/deletions": () => json(200, { configured: true, restored: [] }),
    ...extra,
  });
}

const card = (name: string) => screen.getByRole("listitem", { name });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("Admin users", () => {
  it("lists name, role, state, dates, number of vaults and storage, and no vault names", async () => {
    api();
    renderApp("/admin/users");
    const bob = within(await screen.findByRole("listitem", { name: "Bob" }));
    expect(bob.getByText("@bob")).toBeInTheDocument();
    expect(bob.getByText("User")).toBeInTheDocument();
    expect(bob.getByText("Active")).toBeInTheDocument();
    expect(bob.getByText(/Created 1 Oct 2026/)).toBeInTheDocument();
    expect(bob.getByText(/Never signed in/)).toBeInTheDocument();
    expect(bob.getByText("0 vaults")).toBeInTheDocument();
    expect(bob.getByText("5.3 MB")).toBeInTheDocument();
    const ada = within(card("Ada"));
    expect(ada.getByText("Admin")).toBeInTheDocument();
    expect(ada.getByText(/Last sign-in 20 Oct 2026/)).toBeInTheDocument();
    expect(ada.getByText("2 vaults")).toBeInTheDocument();
    expect(ada.getByText("1.5 KB")).toBeInTheDocument();
    expect(within(card("Root")).getByText("Primary admin")).toBeInTheDocument();
  });

  it("gives the primary admin's row no actions", async () => {
    api();
    renderApp("/admin/users");
    await screen.findByRole("listitem", { name: "Root" });
    expect(within(card("Root")).queryByRole("button")).not.toBeInTheDocument();
  });

  it("deactivates only after the username was typed", async () => {
    const patches: unknown[] = [];
    api(admin, {
      "PATCH /api/admin/users/u2": async (r) => {
        patches.push(await r.json());
        return noContent();
      },
    });
    renderApp("/admin/users");
    await userEvent.click(await within(await screen.findByRole("listitem", { name: "Bob" })).findByRole("button", { name: "Deactivate Bob" }));
    const dialog = screen.getByRole("alertdialog", { name: "Deactivate Bob?" });
    const confirm = within(dialog).getByRole("button", { name: "Deactivate" });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(/Type bob to confirm/), "bo");
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(/Type bob to confirm/), "b");
    await userEvent.click(confirm);
    await vi.waitFor(() => expect(patches).toEqual([{ active: false }]));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("reactivates a deactivated user, again with the username typed", async () => {
    const patches: unknown[] = [];
    api(admin, {
      "GET /api/admin/users": () => json(200, [row(root), row(admin), row(plain, { active: false })]),
      "PATCH /api/admin/users/u2": async (r) => {
        patches.push(await r.json());
        return noContent();
      },
    });
    renderApp("/admin/users");
    const bob = within(await screen.findByRole("listitem", { name: "Bob" }));
    expect(bob.getByText("Deactivated")).toBeInTheDocument();
    await userEvent.click(bob.getByRole("button", { name: "Reactivate Bob" }));
    const dialog = screen.getByRole("alertdialog", { name: "Reactivate Bob?" });
    await userEvent.type(within(dialog).getByLabelText(/Type bob to confirm/), "bob");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reactivate" }));
    await vi.waitFor(() => expect(patches).toEqual([{ active: true }]));
  });

  it("offers 'Delete for a privacy request…' for an active user, not for the admin's own row or the primary admin", async () => {
    api();
    renderApp("/admin/users");
    const bob = within(await screen.findByRole("listitem", { name: "Bob" }));
    expect(bob.getByRole("button", { name: "Delete for a privacy request…" })).toBeInTheDocument();
    expect(within(card("Ada")).queryByRole("button", { name: /Delete/ })).not.toBeInTheDocument();
    expect(within(card("Root")).queryByRole("button", { name: /Delete/ })).not.toBeInTheDocument();
  });

  it("offers it for a deactivated user too", async () => {
    api(admin, { "GET /api/admin/users": () => json(200, [row(root), row(admin), row(plain, { active: false })]) });
    renderApp("/admin/users");
    const bob = within(await screen.findByRole("listitem", { name: "Bob" }));
    expect(bob.getByRole("button", { name: "Delete for a privacy request…" })).toBeInTheDocument();
  });

  it("the dialog says the reminder text and needs the password and the username, then shows the receipt", async () => {
    const bodies: unknown[] = [];
    api(admin, {
      "POST /api/admin/users/u2/delete": async (r) => {
        bodies.push(await r.json());
        return json(200, {
          userId: "u2",
          username: "bob",
          deletedBy: "ada",
          deletedAt: "2026-10-27T10:15:00Z",
          vaults: 2,
          notes: 41,
          apiKeys: 1,
          connections: 0,
          invitations: 1,
          externalProviders: [],
          privacyContact: null,
          listed: true,
        });
      },
    });
    renderApp("/admin/users");
    const bob = within(await screen.findByRole("listitem", { name: "Bob" }));
    await userEvent.click(bob.getByRole("button", { name: "Delete for a privacy request…" }));
    const dialog = screen.getByRole("alertdialog", { name: "Delete Bob for a privacy request?" });
    expect(dialog).toHaveTextContent(
      "Before you go on: make sure the request really comes from this person, and answer within one month (GDPR Art. 12(3)). Their account goes for good, with every vault and note, the history, trash and checkpoints, their API keys and app connections. Nobody can bring it back. Backups are not reached.",
    );
    const confirm = within(dialog).getByRole("button", { name: "Delete" });
    await userEvent.type(within(dialog).getByLabelText(/Type bob to confirm/), "bob");
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "admin password, long");
    await userEvent.click(confirm);
    await vi.waitFor(() => expect(bodies).toEqual([{ password: "admin password, long", confirm: "bob" }]));

    const receipt = await screen.findByRole("dialog", { name: "Deleted for a privacy request" });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(receipt).toHaveTextContent("Vaults: 2");
    expect(receipt).toHaveTextContent("Notes: 41");
    expect(receipt).toHaveTextContent("API keys (incl. revoked): 1");
    expect(within(receipt).getByRole("button", { name: "Download receipt" })).toBeInTheDocument();
    expect(within(receipt).getByRole("button", { name: "Copy reply" })).toBeInTheDocument();
    await userEvent.click(within(receipt).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("shows the server's sentence when the deletion is refused, and no receipt", async () => {
    api(admin, { "POST /api/admin/users/u2/delete": () => json(400, { error: "Your password is wrong." }) });
    renderApp("/admin/users");
    await userEvent.click(within(await screen.findByRole("listitem", { name: "Bob" })).getByRole("button", { name: "Delete for a privacy request…" }));
    const dialog = screen.getByRole("alertdialog");
    await userEvent.type(within(dialog).getByLabelText(/Type bob to confirm/), "bob");
    await userEvent.type(within(dialog).getByLabelText("Your password"), "wrong password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Your password is wrong.");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("shows no restore warning when no deleted user is back", async () => {
    const fetch = api();
    renderApp("/admin/users");
    await screen.findByRole("listitem", { name: "Bob" });
    // Only once the check has answered does its absence mean anything.
    await vi.waitFor(() => expect(fetch.mock.calls.some(([r]) => new URL(r.url).pathname === "/api/admin/deletions")).toBe(true));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText(/A backup restore brought back/)).not.toBeInTheDocument();
    expect(screen.queryByText(/won't be noticed/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete again…" })).not.toBeInTheDocument();
  });

  it("says on the users page when deletions aren't listed, so a restore won't be noticed", async () => {
    api(admin, { "GET /api/admin/deletions": () => json(200, { configured: false, restored: [] }) });
    renderApp("/admin/users");
    expect(
      await screen.findByText(
        "Deleted users aren't listed outside the database (Kumbu:Deletions:Path isn't set), so a backup restore that brings one back won't be noticed.",
      ),
    ).toBeInTheDocument();
  });

  it("shows the server's sentence when the restore check fails", async () => {
    api(admin, { "GET /api/admin/deletions": () => json(403, { error: "The check failed." }) });
    renderApp("/admin/users");
    expect(await screen.findByText("The check failed.")).toBeInTheDocument();
  });

  it("the users page shows the restore warning and 'Delete again…' when restored users exist", async () => {
    const bodies: unknown[] = [];
    let restored = [
      { id: "u7", username: "anna", display: "Anna" },
      { id: "u8", username: "max", display: "Max" },
    ];
    const receipt = (id: string, username: string) => ({
      userId: id,
      username,
      deletedBy: "ada",
      deletedAt: "2026-10-27T10:15:00Z",
      vaults: 1,
      notes: 3,
      apiKeys: 0,
      connections: 0,
      invitations: 0,
      externalProviders: [],
      privacyContact: null,
      listed: true,
    });
    api(admin, {
      "GET /api/admin/deletions": () => json(200, { configured: true, restored }),
      "POST /api/admin/deletions/repeat": async (r) => {
        bodies.push(await r.json());
        restored = [];
        return json(200, [receipt("u7", "anna"), receipt("u8", "max")]);
      },
    });
    renderApp("/admin/users");
    const warning = await screen.findByText("A backup restore brought back 2 deleted user(s): anna, max. Delete them again?");
    // A standing note, not an alert announced on every visit.
    expect(warning.closest('[role="status"]')).not.toBeNull();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Delete again…" }));
    const dialog = screen.getByRole("alertdialog");
    const confirm = within(dialog).getByRole("button", { name: "Delete again" });
    expect(within(dialog).queryByLabelText(/to confirm/)).not.toBeInTheDocument();
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "admin password, long");
    await userEvent.click(confirm);
    await vi.waitFor(() => expect(bodies).toEqual([{ password: "admin password, long" }]));

    // One receipt after the other.
    expect(await screen.findByRole("dialog", { name: "Deleted for a privacy request" })).toHaveTextContent("Username: anna");
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(await screen.findByRole("dialog", { name: "Deleted for a privacy request" })).toHaveTextContent("Username: max");
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    await vi.waitFor(() => expect(screen.queryByText(/A backup restore brought back/)).not.toBeInTheDocument());
  });

  it("changes a role", async () => {
    const patches: unknown[] = [];
    api(admin, {
      "PATCH /api/admin/users/u2": async (r) => {
        patches.push(await r.json());
        return noContent();
      },
    });
    renderApp("/admin/users");
    await userEvent.click(await screen.findByRole("button", { name: "Make Bob an admin" }));
    await vi.waitFor(() => expect(patches).toEqual([{ role: "admin" }]));
  });

  it("offers to make another admin a user, and shows the server's sentence when it refuses", async () => {
    api(root, {
      "PATCH /api/admin/users/u1": () => json(403, { error: "Nope, not today." }),
    });
    renderApp("/admin/users");
    await userEvent.click(await screen.findByRole("button", { name: "Make Ada a user" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Nope, not today.");
  });

  it("offers no demotion of oneself, but still the promotion of others", async () => {
    api();
    renderApp("/admin/users");
    await screen.findByRole("listitem", { name: "Ada" });
    expect(screen.queryByRole("button", { name: "Make Ada a user" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Make Bob an admin" })).toBeInTheDocument();
  });

  it("refetches the session after a role change", async () => {
    const fetch = api(admin, { "PATCH /api/admin/users/u2": () => noContent() });
    renderApp("/admin/users");
    await userEvent.click(await screen.findByRole("button", { name: "Make Bob an admin" }));
    // A session read that comes after the PATCH.
    await vi.waitFor(() => {
      const calls = fetch.mock.calls.map(([r]) => `${r.method} ${new URL(r.url).pathname}`);
      expect(calls.slice(calls.indexOf("PATCH /api/admin/users/u2")).includes("GET /api/session")).toBe(true);
    });
  });

  it("offers no deactivation of oneself", async () => {
    api();
    renderApp("/admin/users");
    await screen.findByRole("listitem", { name: "Ada" });
    expect(within(card("Ada")).queryByRole("button", { name: /Deactivate/ })).not.toBeInTheDocument();
  });

  it("tells a plain user that only an admin can see it, without asking for the data", async () => {
    const fetch = api(plain);
    renderApp("/admin/users");
    expect(await screen.findByText("Only an admin can see this.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to the notes" })).toHaveAttribute("href", "/");
    expect(screen.queryByRole("listitem")).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => new URL(r.url).pathname.startsWith("/api/admin"))).toBe(false);
  });
});
