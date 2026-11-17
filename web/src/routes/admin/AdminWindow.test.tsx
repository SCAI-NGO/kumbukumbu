// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../../state/session";
import { fakeApi, json, renderApp } from "../../test/app";

const admin = { id: "u1", username: "ada", display: "Ada", role: "admin" };
const plain = { id: "u2", username: "bob", display: "Bob", role: "user" };

function api(who = admin) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: who, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, []),
    "GET /api/admin/users": () => json(200, []),
    "GET /api/admin/deletions": () => json(200, { configured: true, restored: [] }),
    "GET /api/admin/invitations": () => json(200, []),
  });
}

beforeEach(() => {
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("the admin window", () => {
  it("is a window with Users and Invitations, and moving between them replaces", async () => {
    api();
    const router = renderApp(["/", "/admin/users"]);
    const window = await screen.findByRole("dialog", { name: "Admin" });
    const nav = within(window).getByRole("navigation", { name: "Admin" });
    expect(within(nav).getAllByRole("link").map((l) => l.textContent)).toEqual(["Users", "Invitations"]);
    expect(within(nav).getByRole("link", { name: "Users" })).toHaveAttribute("aria-current", "page");

    await userEvent.click(within(nav).getByRole("link", { name: "Invitations" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/admin/invitations"));
    expect(await within(window).findByRole("button", { name: "New invitation" })).toBeInTheDocument();
    expect(within(nav).getByRole("link", { name: "Invitations" })).toHaveAttribute("aria-current", "page");

    // One history entry for the whole window: Back leaves it.
    await act(() => router.navigate(-1));
    expect(router.state.location.pathname).toBe("/");
    expect(screen.queryByRole("dialog", { name: "Admin" })).not.toBeInTheDocument();
  });

  it("tells a non-admin, inside the window, that only an admin can see it", async () => {
    api(plain);
    renderApp("/admin/invitations");
    const window = await screen.findByRole("dialog", { name: "Admin" });
    expect(await within(window).findByText("Only an admin can see this.")).toBeInTheDocument();
    expect(within(window).queryByRole("button", { name: "New invitation" })).not.toBeInTheDocument();
  });
});
