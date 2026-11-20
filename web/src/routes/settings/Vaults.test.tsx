// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react";
import { useDiscreet } from "../../discreet/state";
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

beforeEach(() => {
  localStorage.clear();
  delete document.documentElement.dataset.scheme;
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.setState({ gone: new Set(), names: new Set() });
});

describe("Settings: Vaults", () => {
  it("has the sections Vaults, Shared vaults and Published notes", async () => {
    api();
    renderApp("/settings/vaults");
    await screen.findByRole("region", { name: "Vaults" });
    const titles = within(screen.getByRole("dialog", { name: "Settings" })).getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(titles.slice(1)).toEqual(["Vaults", "Shared vaults", "Published notes"]);
  });

  it("shows storage used of the limit", async () => {
    const MB = 1024 ** 2;
    api({ "GET /api/account/storage": () => json(200, { used: 182 * MB, limit: 2048 * MB }) });
    renderApp("/settings/vaults");
    expect(await within(await screen.findByRole("region", { name: "Vaults" })).findByText("Storage: 182 MB of 2 GB")).toBeInTheDocument();
  });

  it("says when the storage has no limit", async () => {
    api({ "GET /api/account/storage": () => json(200, { used: 190_000_000, limit: null }) });
    renderApp("/settings/vaults");
    expect(await within(await screen.findByRole("region", { name: "Vaults" })).findByText("Storage: 181.2 MB (no limit)")).toBeInTheDocument();
  });

  it("re-encrypting a vault lists the keys and apps that lose access first", async () => {
    const bodies: unknown[] = [];
    let release: () => void = () => {};
    const access = new Promise<void>((resolve) => (release = resolve));
    api({
      "GET /api/vaults/Diary/access": async () => (await access, json(200, { apiKeys: ["laptop", "phone"], connections: ["claude"], shares: ["Work PC"], members: [] })),
      "POST /api/vaults/Diary/rekey": async (r) => (bodies.push(await r.json()), json(200, { revokedApiKeys: 2, disconnectedApps: 1, revokedShares: 1 })),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, "invalidateQueries");
    renderApp("/settings/vaults", queryClient);
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Re-encrypt Diary" }));

    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    // No password before the list of what loses access is shown.
    expect(within(dialog).queryByLabelText("Your password")).not.toBeInTheDocument();
    release();
    const keys = await within(dialog).findByRole("list", { name: "API keys" });
    expect(within(keys).getAllByRole("listitem").map((i) => i.textContent)).toEqual(["laptop", "phone"]);
    const apps = within(dialog).getByRole("list", { name: "Connected apps" });
    expect(within(apps).getAllByRole("listitem").map((i) => i.textContent)).toEqual(["claude"]);
    const shares = within(dialog).getByRole("list", { name: "WebDAV shares" });
    expect(within(shares).getAllByRole("listitem").map((i) => i.textContent)).toEqual(["Work PC"]);

    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(bodies).toEqual([{ password: "my password, long" }]);
    expect(within(vaultsSection).getByRole("status")).toHaveTextContent(
      "Diary is re-encrypted. 2 API keys were revoked and 1 app was disconnected. 1 WebDAV share was revoked.",
    );
    expect(invalidated).toHaveBeenCalledWith({ queryKey: ["dav-shares"] });
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("my password");
  });

  it("re-encrypting a vault counts a WebDAV share as something that loses access", async () => {
    api({
      "GET /api/vaults/Diary/access": () => json(200, { apiKeys: [], connections: [], shares: ["Work PC"], members: [] }),
      "POST /api/vaults/Diary/rekey": () => json(200, { revokedApiKeys: 0, disconnectedApps: 0, revokedShares: 1 }),
    });
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Re-encrypt Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    const shares = await within(dialog).findByRole("list", { name: "WebDAV shares" });
    expect(within(shares).getAllByRole("listitem").map((i) => i.textContent)).toEqual(["Work PC"]);
    expect(within(dialog).queryByText(/^No API key/)).not.toBeInTheDocument();

    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(within(vaultsSection).getByRole("status")).toHaveTextContent(/^Diary is re-encrypted\. 1 WebDAV share was revoked\.$/);
  });

  it("names the members' keys and apps that lose the vault too", async () => {
    const member = (label: string, credentials: { name: string; kind: string }[]) => ({
      id: label, label, role: "write", status: "active", confirm: false, acceptedAt: null, redeemedAt: null, expiresAt: null,
      credentials: credentials.map((c) => ({ ...c, lastUsedAt: null })),
    });
    api({
      "GET /api/vaults/Diary/access": () =>
        json(200, { apiKeys: [], connections: [], shares: [], members: [member("Bob", []), member("Carol", [{ name: "Carols Agent", kind: "key" }, { name: "claude", kind: "connection" }])] }),
    });
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Re-encrypt Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    const list = await within(dialog).findByRole("list", { name: "Members' keys and apps" });
    expect(within(list).getAllByRole("listitem").map((i) => i.textContent)).toEqual(["Carol: Carols Agent, claude"]);
    expect(within(dialog).queryByText(/^No API key/)).not.toBeInTheDocument();
  });

  it("says when a vault is busy being re-encrypted", async () => {
    api({
      "GET /api/vaults/Diary/access": () => json(200, { apiKeys: [], connections: [], shares: [], members: [] }),
      "POST /api/vaults/Diary/rekey": () => json(503, { error: "This vault is being re-encrypted; try again in a moment." }),
    });
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Re-encrypt Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    expect(await within(dialog).findByText("No API key, connected app or WebDAV share has access to it.")).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));
    expect(await within(dialog).findByText("This vault is being re-encrypted; try again in a moment.")).toBeInTheDocument();
  });

  it("the vaults section says what deleting and re-encrypting do to API keys and WebDAV shares", async () => {
    api();
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    expect(vaultsSection).toHaveTextContent("API keys and WebDAV shares keep their other vaults; one that had only this one is revoked.");
    expect(vaultsSection).toHaveTextContent(
      "Re-encrypting a vault gives it a new key. API keys, connected apps and WebDAV shares with access to it lose it and need to be set up or reconnected.",
    );
  });

  it("the Delete and Re-encrypt dialogs of a vault hidden again close with it, and so does the re-encrypted line", async () => {
    api({
      "GET /api/keys": () => json(200, []),
      "GET /api/vaults/Diary/access": () => json(200, { apiKeys: [], connections: [], shares: [], members: [] }),
      "GET /api/vaults/Work/access": () => json(200, { apiKeys: [], connections: [], shares: [], members: [] }),
      "POST /api/vaults/Diary/rekey": () => json(200, { revokedApiKeys: 0, disconnectedApps: 0, revokedShares: 0 }),
    });
    const hide = () => act(() => useDiscreet.setState({ gone: new Set(["Diary"]), names: new Set() }));
    const show = () => act(() => useDiscreet.setState({ gone: new Set(), names: new Set(["Diary"]) }));
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });

    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Delete Diary" }));
    expect(screen.getByRole("alertdialog", { name: "Delete Diary?" })).toBeInTheDocument();
    hide();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(screen.queryByText("Delete Diary?")).not.toBeInTheDocument();
    show();

    await userEvent.click(within(vaultsSection).getByRole("button", { name: "Re-encrypt Diary" }));
    expect(screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" })).toBeInTheDocument();
    hide();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    show();

    await userEvent.click(within(vaultsSection).getByRole("button", { name: "Re-encrypt Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    await userEvent.type(await within(dialog).findByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));
    expect(await within(vaultsSection).findByText(/^Diary is re-encrypted\./)).toBeInTheDocument();
    hide();
    expect(within(vaultsSection).queryByText(/^Diary is re-encrypted\./)).not.toBeInTheDocument();
    show();

    // A dialog of another vault stays open.
    await userEvent.click(within(vaultsSection).getByRole("button", { name: "Delete Work" }));
    hide();
    expect(screen.getByRole("alertdialog", { name: "Delete Work?" })).toBeInTheDocument();
  });

  it("a re-encrypt answered after its vault is hidden again shows no re-encrypted line", async () => {
    let answer: () => void = () => {};
    const answered = new Promise<void>((resolve) => (answer = resolve));
    api({
      "GET /api/vaults/Diary/access": () => json(200, { apiKeys: [], connections: [], shares: [], members: [] }),
      "POST /api/vaults/Diary/rekey": async () => (await answered, json(200, { revokedApiKeys: 0, disconnectedApps: 0, revokedShares: 0 })),
    });
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Re-encrypt Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    await userEvent.type(await within(dialog).findByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));

    act(() => useDiscreet.setState({ gone: new Set(["Diary"]), names: new Set() }));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    await act(async () => {
      answer();
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(vaultsSection).not.toHaveTextContent("Diary is re-encrypted");
  });

  it("a pending vault shows as being re-encrypted", async () => {
    api({
      "GET /api/vaults": () =>
        json(200, [
          { name: "Work", notes: 3, embeddings: "internal", pending: false },
          { name: "Diary", notes: 0, embeddings: "internal", pending: true },
        ]),
    });
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    const diary = (await within(vaultsSection).findByText("Diary")).closest("li")!;
    expect(diary).toHaveTextContent("Being re-encrypted");
    expect(diary).not.toHaveTextContent("0 notes");
    expect(within(vaultsSection).getByText("Work").closest("li")).toHaveTextContent("3 notes");
  });

  it("mounts the deletion dialog inside the tap, a field focused, for iOS to raise the keyboard", async () => {
    api({ "GET /api/keys": () => json(200, []) });
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });

    fireEvent.click(await within(vaultsSection).findByRole("button", { name: "Delete Diary" }));

    expect(screen.getByRole("alertdialog", { name: "Delete Diary?" })).toBeInTheDocument();
    expect(screen.getByLabelText("Your password")).toHaveFocus();
  });

  it("deletes a vault only with the password and its name typed exactly", async () => {
    const bodies: unknown[] = [];
    let vaults = [{ name: "Work", notes: 3 }, { name: "Diary", notes: 1 }];
    api({
      "GET /api/vaults": () => json(200, vaults),
      "GET /api/keys": () => json(200, []),
      "POST /api/vaults/Diary/delete": async (r) => {
        bodies.push(await r.json());
        vaults = vaults.filter((v) => v.name !== "Diary");
        return noContent();
      },
    });
    useEditor.getState().openNote("/Diary/today.md");
    renderApp("/settings/vaults");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    expect(await within(vaultsSection).findByText("1 note")).toBeInTheDocument();

    await userEvent.click(within(vaultsSection).getByRole("button", { name: "Delete Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Delete Diary?" });
    const confirm = within(dialog).getByRole("button", { name: "Delete vault" });
    await userEvent.type(within(dialog).getByLabelText("Type Diary to confirm"), "diar");
    expect(confirm).toBeDisabled();
    await userEvent.clear(within(dialog).getByLabelText("Type Diary to confirm"));
    // Case doesn't count: the label reads in capitals, and the point is attention, not spelling.
    await userEvent.type(within(dialog).getByLabelText("Type Diary to confirm"), "DIARY");
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(confirm);

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(bodies).toEqual([{ password: "my password, long", confirm: "Diary" }]);
    await waitFor(() => expect(within(vaultsSection).queryByText("Diary")).not.toBeInTheDocument());
    expect(useEditor.getState().currentPath).toBeNull();
    expect(useEditor.getState().recentPaths).toEqual([]);
  });

  it("shows the server's sentence when the vault deletion is refused, and keeps the password out of the caches", async () => {
    api({ "GET /api/keys": () => json(200, []), "POST /api/vaults/Diary/delete": () => json(400, { error: "The password is wrong." }) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/settings/vaults", queryClient);
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Delete Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Delete Diary?" });
    await userEvent.type(within(dialog).getByLabelText("Type Diary to confirm"), "Diary");
    await userEvent.type(within(dialog).getByLabelText("Your password"), "wrong password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete vault" }));

    expect(await within(dialog).findByText("The password is wrong.")).toBeInTheDocument();
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("wrong password");
  });
});
