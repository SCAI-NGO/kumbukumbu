// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const recoveryKey = "WXYZ-2345-6789-ABCD";

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }, { name: "Diary", notes: 1 }]),
    ...overrides,
  });
}

function mockSystem(dark: boolean) {
  vi.stubGlobal("matchMedia", (q: string) => ({ matches: dark && q.includes("dark"), addEventListener() {}, removeEventListener() {} }));
}

const section = (name: string) => screen.getByRole("region", { name });

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
});

describe("Settings", () => {
  it("follows the system by default and remembers only the scheme's name", async () => {
    mockSystem(true);
    api();
    renderApp("/settings");
    const schemes = await screen.findByRole("radiogroup", { name: "Colour scheme" });
    expect(within(schemes).getByLabelText("Follow the system")).toBeChecked();

    await userEvent.click(within(schemes).getByLabelText("Catppuccin Latte"));
    expect(document.documentElement.dataset.scheme).toBe("catppuccin-latte");
    expect({ ...localStorage }).toEqual({ "kumbu.scheme": "catppuccin-latte" });

    await userEvent.click(within(schemes).getByLabelText("Follow the system"));
    expect(document.documentElement.dataset.scheme).toBe("kumbu");
    expect(localStorage.length).toBe(0);
  });

  it("refreshes Read mode by itself only once chosen, kept on this device", async () => {
    mockSystem(true);
    api();
    renderApp("/settings");
    const box = await within(await screen.findByRole("region", { name: "Changes made elsewhere" })).findByRole("checkbox", { name: "Refresh Read mode automatically" });
    expect(box).not.toBeChecked();

    await userEvent.click(box);
    expect(box).toBeChecked();
    expect({ ...localStorage }).toEqual({ "kumbu.autoRefresh": "on" });

    await userEvent.click(box);
    expect(localStorage.length).toBe(0);
  });

  it("groups the schemes as dark and light, each with a swatch", async () => {
    mockSystem(true);
    api();
    renderApp("/settings");
    const dark = await screen.findByRole("group", { name: "Dark" });
    const light = screen.getByRole("group", { name: "Light" });
    expect(within(dark).getByLabelText("Tokyo Night")).toBeInTheDocument();
    expect(within(dark).queryByLabelText("Tokyo Night Day")).not.toBeInTheDocument();
    expect(within(light).getByLabelText("Tokyo Night Day")).toBeInTheDocument();
    expect(within(light).getByLabelText("Rosé Pine Dawn")).toBeInTheDocument();
    expect(screen.getByTestId("swatch-nord")).toBeInTheDocument();
  });

  it("shows a remembered choice as chosen", async () => {
    mockSystem(false);
    localStorage.setItem("kumbu.scheme", "kumbu");
    api();
    renderApp("/settings");
    expect(await screen.findByLabelText("Kumbu")).toBeChecked();
  });

  it("changes the password, says every session ends, and goes to sign-in", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/password": async (r) => (bodies.push(await r.json()), json(200, { signedOut: true })) });
    const router = renderApp("/settings");
    const form = await screen.findByRole("region", { name: "Password" });
    expect(form).toHaveTextContent(/ends every session/);
    await userEvent.type(within(form).getByLabelText("Current password"), "old password here");
    await userEvent.type(within(form).getByLabelText("New password"), "a much longer new one");
    await userEvent.click(within(form).getByRole("button", { name: "Change password" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(bodies).toEqual([{ oldPassword: "old password here", newPassword: "a much longer new one", revokeApiKeys: true }]);
    expect(await screen.findByRole("status")).toHaveTextContent("Your password is changed");
  });

  it("revokes every API key with the password change unless told to keep them", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/password": async (r) => (bodies.push(await r.json()), json(200, { signedOut: true })) });
    const router = renderApp("/settings");
    const form = await screen.findByRole("region", { name: "Password" });
    const revoke = within(form).getByLabelText("Also revoke all API keys and disconnect apps");
    expect(revoke).toBeChecked();
    expect(form).toHaveTextContent("Agents using them will need new keys, and apps will need to reconnect.");

    await userEvent.click(revoke);
    await userEvent.type(within(form).getByLabelText("Current password"), "old password here");
    await userEvent.type(within(form).getByLabelText("New password"), "a much longer new one");
    await userEvent.click(within(form).getByRole("button", { name: "Change password" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(bodies).toEqual([{ oldPassword: "old password here", newPassword: "a much longer new one", revokeApiKeys: false }]);
  });

  it("shows the server's sentence when the password change is refused", async () => {
    api({ "POST /api/password": () => json(400, { error: "The current password is wrong." }) });
    renderApp("/settings");
    const form = await screen.findByRole("region", { name: "Password" });
    await userEvent.type(within(form).getByLabelText("Current password"), "wrong");
    await userEvent.type(within(form).getByLabelText("New password"), "a much longer new one");
    await userEvent.click(within(form).getByRole("button", { name: "Change password" }));
    expect(await within(form).findByText("The current password is wrong.")).toBeInTheDocument();
  });

  it("makes a new recovery key with the current password and shows it once", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const bodies: unknown[] = [];
    api({ "POST /api/recovery-key": async (r) => (bodies.push(await r.json()), json(200, { recoveryKey })) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/settings", queryClient);
    const part = await screen.findByRole("region", { name: "Recovery key" });
    expect(part).toHaveTextContent(/old one stops working at once/);
    await userEvent.type(within(part).getByLabelText("Current password"), "my password");
    await userEvent.click(within(part).getByRole("button", { name: "Make a new recovery key" }));

    expect(await screen.findByText(recoveryKey)).toBeInTheDocument();
    expect(bodies).toEqual([{ currentPassword: "my password" }]);
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    await userEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect(screen.queryByText(recoveryKey)).not.toBeInTheDocument();
    expect(screen.getByText(/Your new recovery key is in place/)).toBeInTheDocument();
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe("/settings");
    expect(JSON.stringify({ ...localStorage })).not.toContain(recoveryKey);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(recoveryKey));
  });

  it("asks once before leaving with the new recovery key unsaved", async () => {
    api({ "POST /api/recovery-key": () => json(200, { recoveryKey }) });
    const router = renderApp("/settings");
    const part = await screen.findByRole("region", { name: "Recovery key" });
    await userEvent.type(within(part).getByLabelText("Current password"), "my password");
    await userEvent.click(within(part).getByRole("button", { name: "Make a new recovery key" }));
    await screen.findByText(recoveryKey);

    await act(() => router.navigate("/keys"));
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved your recovery key");
    expect(router.state.location.pathname).toBe("/settings");
  });

  it("the re-encrypt section says to change a leaked password", async () => {
    api();
    renderApp("/settings");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    expect(part).toHaveTextContent(
      "New keys for your account and every vault. API keys and connected apps lose access and need to be set up or reconnected; you get a new recovery key. If you think your password leaked, change it too.",
    );
  });

  it("re-encrypting shows the new recovery key once", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    const bodies: unknown[] = [];
    api({ "POST /api/account/reencrypt": async (r) => (bodies.push(await r.json()), json(200, { recoveryKey })) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/settings", queryClient);
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    await userEvent.type(within(part).getByLabelText("Current password"), "my password");
    await userEvent.click(within(part).getByRole("button", { name: "Re-encrypt my vaults" }));

    expect(await within(part).findByText(recoveryKey)).toBeInTheDocument();
    expect(bodies).toEqual([{ password: "my password" }]);
    await userEvent.click(within(part).getByRole("button", { name: "Copy" }));
    await userEvent.click(within(part).getByLabelText(/I have stored my recovery key/));
    await userEvent.click(within(part).getByRole("button", { name: "Continue" }));

    expect(screen.queryByText(recoveryKey)).not.toBeInTheDocument();
    expect(within(part).getByRole("status")).toHaveTextContent("Your vaults are being re-encrypted");
    expect(bodies).toHaveLength(1);
    expect(JSON.stringify({ ...localStorage })).not.toContain(recoveryKey);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(recoveryKey));
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data))).not.toContain(recoveryKey);
  });

  it("shows the server's sentence when re-encrypting is refused", async () => {
    api({ "POST /api/account/reencrypt": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    await userEvent.type(within(part).getByLabelText("Current password"), "wrong");
    await userEvent.click(within(part).getByRole("button", { name: "Re-encrypt my vaults" }));
    expect(await within(part).findByText("The password is wrong.")).toBeInTheDocument();
  });

  it("re-encrypting a vault lists the keys and apps that lose access first", async () => {
    const bodies: unknown[] = [];
    let release: () => void = () => {};
    const access = new Promise<void>((resolve) => (release = resolve));
    api({
      "GET /api/vaults/Diary/access": async () => (await access, json(200, { apiKeys: ["laptop", "phone"], connections: ["claude"] })),
      "POST /api/vaults/Diary/rekey": async (r) => (bodies.push(await r.json()), json(200, { revokedApiKeys: 2, disconnectedApps: 1 })),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/settings", queryClient);
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

    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(bodies).toEqual([{ password: "my password, long" }]);
    expect(within(vaultsSection).getByRole("status")).toHaveTextContent("Diary is re-encrypted. 2 API keys were revoked and 1 app was disconnected.");
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("my password");
  });

  it("says when a vault is busy being re-encrypted", async () => {
    api({
      "GET /api/vaults/Diary/access": () => json(200, { apiKeys: [], connections: [] }),
      "POST /api/vaults/Diary/rekey": () => json(503, { error: "This vault is being re-encrypted; try again in a moment." }),
    });
    renderApp("/settings");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Re-encrypt Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Re-encrypt Diary?" });
    expect(await within(dialog).findByText("No API key or connected app has access to it.")).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Re-encrypt vault" }));
    expect(await within(dialog).findByText("This vault is being re-encrypted; try again in a moment.")).toBeInTheDocument();
  });

  it("a pending vault shows as being re-encrypted", async () => {
    api({
      "GET /api/vaults": () =>
        json(200, [
          { name: "Work", notes: 3, embeddings: "internal", pending: false },
          { name: "Diary", notes: 0, embeddings: "internal", pending: true },
        ]),
    });
    renderApp("/settings");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    const diary = (await within(vaultsSection).findByText("Diary")).closest("li")!;
    expect(diary).toHaveTextContent("Being re-encrypted");
    expect(diary).not.toHaveTextContent("0 notes");
    expect(within(vaultsSection).getByText("Work").closest("li")).toHaveTextContent("3 notes");
  });

  it("signs out the other sessions", async () => {
    let ended = 0;
    api({ "POST /api/sessions/end-others": () => (ended++, noContent()) });
    renderApp("/settings");
    await screen.findByRole("region", { name: "Sessions" });
    await userEvent.click(within(section("Sessions")).getByRole("button", { name: "Sign out other sessions" }));
    expect(await within(section("Sessions")).findByText("Every other session has ended.")).toBeInTheDocument();
    expect(ended).toBe(1);
  });

  it("mounts the deletion dialog inside the tap, a field focused, for iOS to raise the keyboard", async () => {
    api({ "GET /api/keys": () => json(200, []) });
    renderApp("/settings");
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
    renderApp("/settings");
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    expect(await within(vaultsSection).findByText("1 note")).toBeInTheDocument();

    await userEvent.click(within(vaultsSection).getByRole("button", { name: "Delete Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Delete Diary?" });
    const confirm = within(dialog).getByRole("button", { name: "Delete vault" });
    await userEvent.type(within(dialog).getByLabelText("Type Diary to confirm"), "diary");
    expect(confirm).toBeDisabled();
    await userEvent.clear(within(dialog).getByLabelText("Type Diary to confirm"));
    await userEvent.type(within(dialog).getByLabelText("Type Diary to confirm"), "Diary");
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
    renderApp("/settings", queryClient);
    const vaultsSection = await screen.findByRole("region", { name: "Vaults" });
    await userEvent.click(await within(vaultsSection).findByRole("button", { name: "Delete Diary" }));
    const dialog = screen.getByRole("alertdialog", { name: "Delete Diary?" });
    await userEvent.type(within(dialog).getByLabelText("Type Diary to confirm"), "Diary");
    await userEvent.type(within(dialog).getByLabelText("Your password"), "wrong password, long");
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete vault" }));

    expect(await within(dialog).findByText("The password is wrong.")).toBeInTheDocument();
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("wrong password");
  });

  it("deletes the account with the password and the username typed, and goes to sign-in", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/account/delete": async (r) => (bodies.push(await r.json()), noContent()) });
    const router = renderApp("/settings");
    const form = await screen.findByRole("region", { name: "Delete account" });
    const button = within(form).getByRole("button", { name: "Delete my account" });
    await userEvent.type(within(form).getByLabelText("Current password"), "my password, long");
    expect(button).toBeDisabled();
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(button);

    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(bodies).toEqual([{ password: "my password, long", confirm: "ada" }]);
    expect(await screen.findByRole("status")).toHaveTextContent("Your account is deleted");
  });

  it("shows the server's sentence when the account deletion is refused", async () => {
    api({ "POST /api/account/delete": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings");
    const form = await screen.findByRole("region", { name: "Delete account" });
    await userEvent.type(within(form).getByLabelText("Current password"), "wrong password, long");
    await userEvent.type(within(form).getByLabelText("Type ada to confirm"), "ada");
    await userEvent.click(within(form).getByRole("button", { name: "Delete my account" }));
    expect(await within(form).findByText("The password is wrong.")).toBeInTheDocument();
  });

  it("offers the primary admin no account deletion", async () => {
    api({ "GET /api/session": () => json(200, { user: { ...user, role: "primary_admin" }, previousSignInAt: null }) });
    renderApp("/settings");
    const form = await screen.findByRole("region", { name: "Delete account" });
    expect(form).toHaveTextContent("the server always needs one");
    expect(within(form).queryByRole("button")).not.toBeInTheDocument();
  });
});
