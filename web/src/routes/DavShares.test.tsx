// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const vaults = [
  { name: "Work", notes: 3 },
  { name: "Personal", notes: 1 },
];
const workPc = {
  id: "k7qm",
  username: "ada.k7qm",
  label: "Work PC",
  vaults: { Work: "write", Personal: "read" },
  createdAt: "2026-10-01T09:00:00Z",
  lastUsedAt: null as string | null,
  revokedAt: null as string | null,
  expiresAt: null as string | null,
  lostVaults: [] as string[],
};
const home = { ...workPc, id: "h3x9", username: "ada.h3x9", label: "Home", vaults: { Personal: "maintain" }, lastUsedAt: "2026-10-05T18:30:00Z" };

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}, enabled = true, shares: unknown[] = [workPc, home]) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/dav-shares": () => json(200, { enabled, shares }),
    ...overrides,
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("DavShares", () => {
  it("lists shares", async () => {
    api();
    renderApp("/access/dav");
    const panel = await screen.findByRole("tabpanel", { name: "WebDAV" });
    const item = (await within(panel).findByText("Work PC")).closest("li")!;
    expect(item).toHaveTextContent("ada.k7qm");
    expect(item).toHaveTextContent("Work: read and write");
    expect(item).toHaveTextContent("Personal: read");
    expect(item).toHaveTextContent("Created 1 Oct 2026");
    expect(item).toHaveTextContent("Never used");
    expect(item).toHaveTextContent("Doesn't expire");
    expect(within(item).getByRole("button", { name: "Revoke Work PC" })).toBeInTheDocument();
    expect(within(item).getByRole("button", { name: "How to connect Work PC" })).toBeInTheDocument();
    expect(screen.getByText("Home").closest("li")).toHaveTextContent("Personal: read, write and maintain");
    expect(screen.queryByText(/switched off/)).not.toBeInTheDocument();

    // Least recently used first: the never used one leads either way; sorting must not drop one.
    await userEvent.click(screen.getByRole("button", { name: "Least recently used first" }));
    expect(within(panel).getAllByRole("heading", { level: 3 }).map((h) => h.textContent)).toEqual(["Work PC", "Home"]);
  });

  it("says when WebDAV is off", async () => {
    api({}, false);
    renderApp("/access/dav");
    expect(await screen.findByText("WebDAV is switched off on this server; shares work once it is on.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New share" })).toBeEnabled();
  });

  it("says when there is no share yet", async () => {
    api({}, true, []);
    renderApp("/access/dav");
    expect(await screen.findByText("You have no WebDAV shares yet.")).toBeInTheDocument();
  });

  it("marks a share that lost a vault", async () => {
    api({}, true, [{ ...workPc, lostVaults: ["v-gone"] }, home]);
    renderApp("/access/dav");
    const item = (await screen.findByText("Work PC")).closest("li")!;
    expect(within(item).getByRole("note")).toHaveTextContent("This share lost a vault when it was re-encrypted");
    expect(screen.getByText("Home").closest("li")).not.toHaveTextContent(/lost/);
  });

  it("shows a revoked or expired share without its buttons", async () => {
    api({}, true, [
      { ...workPc, revokedAt: "2026-10-02T09:00:00Z" },
      { ...home, expiresAt: "2026-01-31T09:00:00Z" },
    ]);
    renderApp("/access/dav");
    const revoked = (await screen.findByText("Work PC")).closest("li")!;
    expect(revoked).toHaveTextContent("Revoked 2 Oct 2026");
    expect(within(revoked).queryByRole("button")).not.toBeInTheDocument();
    const expired = screen.getByText("Home").closest("li")!;
    expect(expired).toHaveTextContent("Expired 31 Jan 2026");
    expect(within(expired).queryByRole("button")).not.toBeInTheDocument();
  });

  it("revoke dialog says drives stop working", async () => {
    let revoked = 0;
    api({ "DELETE /api/dav-shares/k7qm": () => (revoked++, noContent()) });
    renderApp("/access/dav");
    await userEvent.click(await screen.findByRole("button", { name: "Revoke Work PC" }));
    const confirm = screen.getByRole("alertdialog", { name: "Revoke Work PC?" });
    expect(confirm).toHaveTextContent("Drives mounted with this share stop working");
    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(revoked).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Revoke Work PC" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Revoke" }));
    await waitFor(() => expect(revoked).toBe(1));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
  });
});
