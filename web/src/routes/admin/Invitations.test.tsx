// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../../state/session";
import { fakeApi, json, noContent, renderApp } from "../../test/app";

const admin = { id: "u1", username: "ada", display: "Ada", role: "admin" };
const link = "https://kumbu.example/join#kumbu-inv_Zx81Qp";

const open = {
  id: "i1",
  role: "user",
  note: "For Bob",
  createdBy: "u1",
  createdAt: "2026-10-20T09:00:00Z",
  expiresAt: "2026-10-27T09:00:00Z",
  usedAt: null,
  usedBy: null,
  revokedAt: null,
  state: "open",
};
const used = { ...open, id: "i0", note: null, role: "admin", state: "used", usedAt: "2026-10-21T09:00:00Z", usedBy: "u2" };

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: admin, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, []),
    "GET /api/admin/invitations": () => json(200, [used, open]),
    ...extra,
  });
}

function stubClipboard(writeText = vi.fn(async () => {})) {
  Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
  return writeText;
}

async function createInvitation(queryClient?: QueryClient) {
  const bodies: unknown[] = [];
  const fetch = api({
    "POST /api/admin/invitations": async (r) => {
      bodies.push(await r.json());
      return json(201, { code: "kumbu-inv_Zx81Qp", link, invitation: { ...open, id: "i2", note: "Hi" } });
    },
  });
  const router = renderApp("/admin/invitations", queryClient);
  await userEvent.click(await screen.findByRole("button", { name: "New invitation" }));
  return { fetch, router, bodies, dialog: screen.getByRole("dialog", { name: "New invitation" }) };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("Admin invitations", () => {
  it("lists invitations with role, state, expiry and note", async () => {
    api();
    renderApp("/admin/invitations");
    const first = within(await screen.findByRole("listitem", { name: "Invitation for a user" }));
    expect(first.getByText("Open")).toBeInTheDocument();
    expect(first.getByText("For Bob")).toBeInTheDocument();
    expect(first.getByText(/Expires 27 Oct 2026/)).toBeInTheDocument();
    const second = within(screen.getByRole("listitem", { name: "Invitation for an admin" }));
    expect(second.getByText("Used")).toBeInTheDocument();
    expect(second.queryByRole("button", { name: /Revoke/ })).not.toBeInTheDocument();
  });

  it("does not ask a plain user's session for invitations", async () => {
    const fetch = api({ "GET /api/session": () => json(200, { user: { ...admin, role: "user" }, previousSignInAt: null }) });
    renderApp("/admin/invitations");
    expect(await screen.findByText("Only an admin can see this.")).toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => new URL(r.url).pathname.startsWith("/api/admin"))).toBe(false);
  });

  it("links the two admin pages", async () => {
    api();
    renderApp("/admin/invitations");
    expect(await screen.findByRole("link", { name: "Users" })).toHaveAttribute("href", "/admin/users");
  });

  it("revokes an open invitation after asking", async () => {
    let revoked = false;
    api({
      "GET /api/admin/invitations": () => json(200, revoked ? [used] : [used, open]),
      "DELETE /api/admin/invitations/i1": () => {
        revoked = true;
        return noContent();
      },
    });
    renderApp("/admin/invitations");
    await userEvent.click(await screen.findByRole("button", { name: "Revoke invitation for a user" }));
    const dialog = screen.getByRole("alertdialog", { name: "Revoke this invitation?" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Revoke" }));
    await waitFor(() => expect(screen.queryByRole("listitem", { name: "Invitation for a user" })).not.toBeInTheDocument());
  });

  it("asks for role, expiry (1, 7, 30 days) and a note, defaulting to a user for 7 days", async () => {
    const { dialog, bodies } = await createInvitation();
    expect(within(dialog).getByLabelText("User")).toBeChecked();
    expect(within(dialog).getByLabelText("7 days")).toBeChecked();
    expect(within(dialog).getByLabelText("1 day")).toBeInTheDocument();
    expect(within(dialog).getByLabelText("30 days")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByLabelText("Admin"));
    await userEvent.click(within(dialog).getByLabelText("30 days"));
    await userEvent.type(within(dialog).getByLabelText("Note (optional)"), "Hi");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    await screen.findByText(link);
    expect(bodies).toEqual([{ role: "admin", expiresInDays: 30, note: "Hi" }]);
  });

  it("sends no note when none was typed", async () => {
    const { dialog, bodies } = await createInvitation();
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    await screen.findByText(link);
    expect(bodies).toEqual([{ role: "user", expiresInDays: 7 }]);
  });

  it("marks the copied link", async () => {
    stubClipboard();
    const { dialog } = await createInvitation();
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    await userEvent.click(await screen.findByRole("button", { name: "Copy link" }));
    expect(screen.getByRole("button", { name: "Copy link" })).toHaveTextContent("Copied");
    expect(screen.getByRole("status")).toHaveTextContent(/^Copied$/);
  });

  it("shows the link and a QR code once, and copies the link", async () => {
    const writeText = stubClipboard();
    const { dialog } = await createInvitation();
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    expect(await screen.findByText(link)).toBeInTheDocument();
    expect(await screen.findByRole("img", { name: "QR code for the invitation link" })).toHaveAttribute("src", expect.stringMatching(/^data:image\/svg\+xml/));
    await userEvent.click(screen.getByRole("button", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledWith(link);
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByText(link)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New invitation" })).toBeInTheDocument();
  });

  it("asks once before leaving without having copied the link", async () => {
    const { dialog } = await createInvitation();
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    await screen.findByText(link);
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    const ask = screen.getByRole("alertdialog", { name: "You haven't saved the invitation link" });
    await userEvent.click(within(ask).getByRole("button", { name: "Stay" }));
    expect(screen.getByText(link)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Leave without it" }));
    expect(screen.queryByText(link)).not.toBeInTheDocument();
  });

  it("asks when navigating away before the link was copied", async () => {
    const { dialog, router } = await createInvitation();
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    await screen.findByText(link);
    await act(() => router.navigate("/"));
    expect(screen.getByRole("alertdialog", { name: "You haven't saved the invitation link" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/admin/invitations");
  });

  it("keeps the link out of the mutation cache", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { dialog } = await createInvitation(queryClient);
    await userEvent.click(within(dialog).getByRole("button", { name: "Create invitation" }));
    await screen.findByText(link);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain("kumbu-inv_Zx81Qp"));
  });

  it("shows the server's sentence when creating fails", async () => {
    api({ "POST /api/admin/invitations": () => json(400, { error: "An invitation is valid for 1 to 30 days." }) });
    renderApp("/admin/invitations");
    await userEvent.click(await screen.findByRole("button", { name: "New invitation" }));
    await userEvent.click(screen.getByRole("button", { name: "Create invitation" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("An invitation is valid for 1 to 30 days.");
  });
});
