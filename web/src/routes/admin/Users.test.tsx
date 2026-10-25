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
