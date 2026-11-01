// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import PasswordPrompt from "../components/PasswordPrompt";
import { usePasswordPrompt } from "../state/passwordPrompt";
import { fakeApi, json, noContent } from "../test/app";
import AccessList from "./AccessList";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  act(() => usePasswordPrompt.getState().settle(false));
});

const member = (extra: Record<string, unknown> = {}) => ({
  id: "m1",
  label: "Bob",
  role: "read",
  status: "active",
  confirm: false,
  acceptedAt: "2026-10-30T09:00:00Z",
  redeemedAt: "2026-10-30T09:00:00Z",
  expiresAt: null,
  credentials: [],
  ...extra,
});

let members: ReturnType<typeof member>[] = [];

function accessApi(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/vaults/Gartenarbeit/access": () => json(200, { apiKeys: [], connections: [], members }),
    "GET /api/vaults": () => json(200, []),
    ...extra,
  });
}

function renderList() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <AccessList vault="Gartenarbeit" onClose={() => {}} />
      <PasswordPrompt />
    </QueryClientProvider>,
  );
}

const bob = () => screen.findByRole("listitem", { name: "Bob" });
const requests = (fetch: ReturnType<typeof fakeApi>, method: string) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method);

describe("AccessList", () => {
  it("raises a role after the password dialog", async () => {
    members = [member()];
    let puts = 0;
    const fetch = accessApi({
      "PUT /api/vaults/Gartenarbeit/memberships/m1/role": () => {
        // The first try finds the password window closed; the repeat after the password goes through.
        if (puts++ === 0) return json(401, { error: "Give your password again.", type: "password-required" });
        members = [member({ role: "write" })];
        return json(200, { id: "m1", label: "Bob", role: "write", status: "active", confirm: false, expiresAt: null, redeemedAt: null, redeemer: null });
      },
      "POST /api/session/confirm": () => noContent(),
    });
    renderList();
    await userEvent.selectOptions(within(await bob()).getByLabelText("Role"), "write");

    const prompt = await screen.findByRole("alertdialog", { name: "Confirm your password" });
    await userEvent.type(within(prompt).getByLabelText("Your password"), "correct horse");
    await userEvent.click(within(prompt).getByRole("button", { name: "Confirm" }));

    await waitFor(() => expect(within(screen.getByRole("listitem", { name: "Bob" })).getByLabelText("Role")).toHaveValue("write"));
    const sent = requests(fetch, "PUT");
    expect(sent).toHaveLength(2);
    expect(await sent[1].json()).toEqual({ role: "write" });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("confirms showing the username", async () => {
    members = [member({ status: "pending", confirm: true, acceptedAt: null })];
    const fetch = accessApi({
      "GET /api/vaults/Gartenarbeit/memberships/m1": () =>
        json(200, { id: "m1", label: "Bob", role: "read", status: "pending", confirm: true, expiresAt: null, redeemedAt: "2026-10-30T09:00:00Z", redeemer: { name: '"Bob Example"', username: "bob" } }),
      "POST /api/vaults/Gartenarbeit/memberships/m1/confirm": () => {
        members = [member()];
        return json(200, { id: "m1", label: "Bob", role: "read", status: "active", confirm: true, expiresAt: null, redeemedAt: null, redeemer: null });
      },
    });
    renderList();
    const item = await bob();
    expect(await within(item).findByText((_, el) => el?.tagName === "P" && el.textContent === '"Bob Example" (username bob) accepted Bob\'s invitation — grant access?')).toBeInTheDocument();
    await userEvent.click(within(item).getByRole("button", { name: "Grant access" }));
    await waitFor(() => expect(within(screen.getByRole("listitem", { name: "Bob" })).getByLabelText("Role")).toBeInTheDocument());
    expect(requests(fetch, "POST").map((r) => new URL(r.url).pathname)).toEqual(["/api/vaults/Gartenarbeit/memberships/m1/confirm"]);
  });

  it("revokes", async () => {
    members = [member({ role: "write" })];
    const fetch = accessApi({
      "POST /api/vaults/Gartenarbeit/memberships/m1/revoke": () => {
        members = [];
        return noContent();
      },
    });
    renderList();
    await userEvent.click(within(await bob()).getByRole("button", { name: "Revoke…" }));
    const dialog = screen.getByRole("alertdialog", { name: "Revoke Bob's access to Gartenarbeit?" });
    // Offered, off, and not yet possible while the vault is shared; with the concept's sentence.
    const rekey = within(dialog).getByRole("checkbox", { name: "Also re-encrypt this vault" });
    expect(rekey).not.toBeChecked();
    expect(rekey).toBeDisabled();
    expect(rekey).toHaveAccessibleDescription(/^Only needed if Bob could get hold of an old backup of this server\. Everyone's app connections and keys for this vault stop working and need to be set up again\./);
    await userEvent.click(within(dialog).getByRole("button", { name: "Revoke" }));

    expect(await screen.findByText("Gartenarbeit isn't shared with anyone.")).toBeInTheDocument();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(requests(fetch, "POST").map((r) => new URL(r.url).pathname)).toEqual(["/api/vaults/Gartenarbeit/memberships/m1/revoke"]);
  });

  it("lists a member's keys and apps with their last use on this vault, and maintain in the warning colour", async () => {
    members = [
      member({
        role: "maintain",
        credentials: [
          { name: "Claude", kind: "connection", lastUsedAt: "2026-10-31T08:15:00Z" },
          { name: "Laptop", kind: "key", lastUsedAt: null },
        ],
      }),
    ];
    accessApi();
    renderList();
    const item = await bob();
    const list = within(item).getByRole("list", { name: "Keys and apps of Bob" });
    expect(within(list).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      expect.stringMatching(/^Claude · App · last used here 31 Oct 2026/),
      "Laptop · API key · not used here yet",
    ]);
    expect(within(item).getByLabelText("Role")).toHaveClass("text-warning");
    expect(within(item).getByText("Can change the vault's structure: the board's columns, checkpoints.")).toHaveClass("text-warning");
  });

  it("withdraws an open invitation, and declines a redeemer", async () => {
    members = [
      member({ status: "pending", acceptedAt: null, redeemedAt: null, expiresAt: "2026-11-07T09:00:00Z" }),
      member({ id: "m2", label: "Carol", status: "pending", confirm: true, acceptedAt: null }),
    ];
    const fetch = accessApi({
      "GET /api/vaults/Gartenarbeit/memberships/m2": () =>
        json(200, { id: "m2", label: "Carol", role: "read", status: "pending", confirm: true, expiresAt: null, redeemedAt: "2026-10-30T09:00:00Z", redeemer: { name: '"Mallory"', username: "mallory" } }),
      "DELETE /api/vaults/Gartenarbeit/memberships/m1": () => noContent(),
      "POST /api/vaults/Gartenarbeit/memberships/m2/decline": () => noContent(),
    });
    renderList();
    const invited = await bob();
    expect(invited).toHaveTextContent(/Invited as Read; the link works until 7 Nov 2026/);
    await userEvent.click(within(invited).getByRole("button", { name: "Withdraw" }));
    const carol = screen.getByRole("listitem", { name: "Carol" });
    expect(await within(carol).findByText("mallory")).toBeInTheDocument();
    await userEvent.click(within(carol).getByRole("button", { name: "Decline" }));
    await waitFor(() => expect(requests(fetch, "POST")).toHaveLength(1));
    expect(requests(fetch, "DELETE").map((r) => new URL(r.url).pathname)).toEqual(["/api/vaults/Gartenarbeit/memberships/m1"]);
    expect(new URL(requests(fetch, "POST")[0].url).pathname).toBe("/api/vaults/Gartenarbeit/memberships/m2/decline");
  });
});
