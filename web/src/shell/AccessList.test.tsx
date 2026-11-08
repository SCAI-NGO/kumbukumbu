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

  it("choosing maintain asks for the password and a cancel leaves the role", async () => {
    members = [member()];
    const fetch = accessApi();
    renderList();
    await userEvent.selectOptions(within(await bob()).getByLabelText("Role"), "maintain");

    const dialog = await screen.findByRole("alertdialog", { name: "Let Bob maintain Gartenarbeit?" });
    expect(dialog).toHaveTextContent("Can change the vault's structure: the board's columns, checkpoints.");
    expect(within(dialog).getByRole("button", { name: "Raise to maintain" })).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse");
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(within(await bob()).getByLabelText("Role")).toHaveValue("read");
    expect(requests(fetch, "PUT")).toHaveLength(0);
    // Asked again, the field starts empty.
    await userEvent.selectOptions(within(await bob()).getByLabelText("Role"), "maintain");
    expect(within(await screen.findByRole("alertdialog")).getByLabelText("Your password")).toHaveValue("");
  });

  it("raises to maintain with the password in the request, a wrong one changing nothing", async () => {
    members = [member()];
    let puts = 0;
    const fetch = accessApi({
      "PUT /api/vaults/Gartenarbeit/memberships/m1/role": () => {
        if (puts++ === 0) return json(400, { error: "The password is wrong." });
        members = [member({ role: "maintain" })];
        return json(200, { id: "m1", label: "Bob", role: "maintain", status: "active", confirm: false, expiresAt: null, redeemedAt: null, redeemer: null });
      },
    });
    renderList();
    await userEvent.selectOptions(within(await bob()).getByLabelText("Role"), "maintain");
    const dialog = await screen.findByRole("alertdialog", { name: "Let Bob maintain Gartenarbeit?" });

    await userEvent.type(within(dialog).getByLabelText("Your password"), "not it");
    await userEvent.click(within(dialog).getByRole("button", { name: "Raise to maintain" }));
    expect(await within(dialog).findByText("The password is wrong.")).toBeInTheDocument();
    expect(within(screen.getByRole("listitem", { name: "Bob" })).getByLabelText("Role")).toHaveValue("read");

    await userEvent.clear(within(dialog).getByLabelText("Your password"));
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse");
    await userEvent.click(within(dialog).getByRole("button", { name: "Raise to maintain" }));

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    await waitFor(() => expect(within(screen.getByRole("listitem", { name: "Bob" })).getByLabelText("Role")).toHaveValue("maintain"));
    const sent = requests(fetch, "PUT");
    expect(sent).toHaveLength(2);
    expect(await sent[1].json()).toEqual({ role: "maintain", password: "correct horse" });
    // The password dialog of the window is never involved.
    expect(screen.queryByRole("alertdialog", { name: "Confirm your password" })).not.toBeInTheDocument();
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

  it("offers re-encryption after revoke, off by default, with its sentence", async () => {
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
    const rekey = within(dialog).getByRole("checkbox", { name: "Also re-encrypt this vault" });
    expect(rekey).not.toBeChecked();
    expect(rekey).toBeEnabled();
    expect(rekey).toHaveAccessibleDescription(
      "Only needed if Bob could get hold of an old backup of this server. It doesn't help if Bob can also change this server's database. Everyone's app connections and keys for this vault stop working and need to be set up again.",
    );
    // Off, it asks for no password and runs nothing but the revoke.
    expect(within(dialog).queryByLabelText("Your password")).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Revoke" }));

    expect(await screen.findByText("Gartenarbeit isn't shared with anyone.")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Bob's access is revoked.");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(requests(fetch, "POST").map((r) => new URL(r.url).pathname)).toEqual(["/api/vaults/Gartenarbeit/memberships/m1/revoke"]);
  });

  it("re-encrypts once the revoke is done, the password checked before anything", async () => {
    members = [member({ role: "write" })];
    let confirms = 0;
    const fetch = accessApi({
      "POST /api/session/confirm": () => (confirms++ === 0 ? json(400, { error: "The password is wrong." }) : noContent()),
      "POST /api/vaults/Gartenarbeit/memberships/m1/revoke": () => {
        members = [];
        return noContent();
      },
      "POST /api/vaults/Gartenarbeit/rekey": () => json(200, { documents: 3, damaged: 0, revokedApiKeys: 0, disconnectedApps: 0 }),
    });
    renderList();
    await userEvent.click(within(await bob()).getByRole("button", { name: "Revoke…" }));
    const dialog = screen.getByRole("alertdialog", { name: "Revoke Bob's access to Gartenarbeit?" });
    await userEvent.click(within(dialog).getByRole("checkbox", { name: "Also re-encrypt this vault" }));
    expect(within(dialog).getByRole("button", { name: "Revoke" })).toBeDisabled();

    // A wrong password revokes nothing.
    await userEvent.type(within(dialog).getByLabelText("Your password"), "not it");
    await userEvent.click(within(dialog).getByRole("button", { name: "Revoke" }));
    expect(await within(dialog).findByText("The password is wrong.")).toBeInTheDocument();
    expect(requests(fetch, "POST").map((r) => new URL(r.url).pathname)).toEqual(["/api/session/confirm"]);

    await userEvent.clear(within(dialog).getByLabelText("Your password"));
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse");
    await userEvent.click(within(dialog).getByRole("button", { name: "Revoke" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Bob's access is revoked, and Gartenarbeit is re-encrypted.");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    const sent = requests(fetch, "POST");
    expect(sent.map((r) => new URL(r.url).pathname)).toEqual([
      "/api/session/confirm",
      "/api/session/confirm",
      "/api/vaults/Gartenarbeit/memberships/m1/revoke",
      "/api/vaults/Gartenarbeit/rekey",
    ]);
    expect(await sent[3].json()).toEqual({ password: "correct horse" });
  });

  it("offers the owner re-encryption after a member left, and runs nothing until she asks", async () => {
    members = [];
    let left = [{ label: "Bob", leftAt: "2026-10-31T08:15:00Z" }];
    const fetch = fakeApi({
      "GET /api/vaults/Gartenarbeit/access": () => json(200, { apiKeys: [], connections: [], members, left }),
      "GET /api/vaults": () => json(200, []),
      "POST /api/session/confirm": () => noContent(),
      "POST /api/vaults/Gartenarbeit/rekey": () => {
        left = [];
        return json(200, { documents: 3, damaged: 0, revokedApiKeys: 0, disconnectedApps: 0 });
      },
    });
    renderList();
    const offer = await screen.findByRole("region", { name: /^Bob left Gartenarbeit 31 Oct 2026/ });
    const button = within(offer).getByRole("button", { name: "Re-encrypt Gartenarbeit…" });
    expect(button).toHaveAccessibleDescription(
      "Only needed if Bob could get hold of an old backup of this server. It doesn't help if Bob can also change this server's database. Everyone's app connections and keys for this vault stop working and need to be set up again.",
    );
    expect(requests(fetch, "POST")).toHaveLength(0);

    await userEvent.click(button);
    await userEvent.type(within(offer).getByLabelText("Your password"), "correct horse");
    await userEvent.click(within(offer).getByRole("button", { name: "Re-encrypt vault" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Gartenarbeit is re-encrypted.");
    await waitFor(() => expect(screen.queryByRole("region")).not.toBeInTheDocument());
    expect(requests(fetch, "POST").map((r) => new URL(r.url).pathname)).toEqual(["/api/session/confirm", "/api/vaults/Gartenarbeit/rekey"]);
  });

  it("lists a member's keys, apps and WebDAV shares with their last use on this vault, and maintain in the warning colour", async () => {
    members = [
      member({
        role: "maintain",
        credentials: [
          { name: "Claude", kind: "connection", lastUsedAt: "2026-10-31T08:15:00Z" },
          { name: "Laptop", kind: "key", lastUsedAt: null },
          { name: "Work PC (dav)", kind: "share", lastUsedAt: null },
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
      "Work PC (dav) · WebDAV share · not used here yet",
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

  it("closing the invitation dialog shows the list, updated", async () => {
    members = [];
    accessApi({
      "POST /api/vaults/Gartenarbeit/invitations": () => {
        // Bob opens the link while the owner still has the dialog with it open.
        setTimeout(() => (members = [member({ status: "pending", confirm: true, acceptedAt: null })]), 0);
        return json(201, { id: "m1", link: "https://kumbu.example/share#c2VjcmV0LWZvci10aGUtdGVzdA", role: "read", confirm: true, expiresAt: "2026-11-08T10:00:00Z" });
      },
      "GET /api/vaults/Gartenarbeit/memberships/m1": () =>
        json(200, { id: "m1", label: "Bob", role: "read", status: "pending", confirm: true, expiresAt: null, redeemedAt: "2026-10-30T09:00:00Z", redeemer: { name: '"Bob"', username: "bob" } }),
    });
    renderList();
    expect(await screen.findByText("Gartenarbeit isn't shared with anyone.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Invite someone…" }));
    await userEvent.type(screen.getByLabelText("Who it is for"), "Bob");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));
    await userEvent.type(screen.getByLabelText("Your password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Create link" }));
    const share = await screen.findByRole("dialog", { name: "Share Gartenarbeit" });
    await new Promise((resolve) => setTimeout(resolve, 20));
    await userEvent.click(within(share).getByRole("button", { name: "Done" }));
    expect(await bob()).toHaveTextContent(/accepted|Grant access/);
    expect(screen.queryByText("Gartenarbeit isn't shared with anyone.")).not.toBeInTheDocument();
  });

  it("a redeemer who arrives shows without a reload", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"], shouldAdvanceTime: true });
    try {
      members = [member({ status: "pending", acceptedAt: null, redeemedAt: null, expiresAt: "2026-11-07T09:00:00Z" })];
      accessApi({
        "GET /api/vaults/Gartenarbeit/memberships/m1": () =>
          json(200, { id: "m1", label: "Bob", role: "read", status: "pending", confirm: true, expiresAt: null, redeemedAt: "2026-10-30T09:00:00Z", redeemer: { name: '"Bob"', username: "bob" } }),
      });
      renderList();
      expect(await within(await bob()).findByText(/Invited as Read/)).toBeInTheDocument();
      members = [member({ status: "pending", confirm: true, acceptedAt: null })];
      await act(() => vi.advanceTimersByTimeAsync(10_500));
      expect(await within(await bob()).findByRole("button", { name: "Grant access" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});
