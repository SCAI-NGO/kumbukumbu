// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
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
    const revoke = within(form).getByLabelText("Also revoke all API keys");
    expect(revoke).toBeChecked();
    expect(form).toHaveTextContent("Agents using them will need new keys.");

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

  it("signs out the other sessions", async () => {
    let ended = 0;
    api({ "POST /api/sessions/end-others": () => (ended++, noContent()) });
    renderApp("/settings");
    await screen.findByRole("region", { name: "Sessions" });
    await userEvent.click(within(section("Sessions")).getByRole("button", { name: "Sign out other sessions" }));
    expect(await within(section("Sessions")).findByText("Every other session has ended.")).toBeInTheDocument();
    expect(ended).toBe(1);
  });

  it("deletes a vault only after its name was typed exactly", async () => {
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
    await userEvent.click(confirm);

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(bodies).toEqual([{ confirm: "Diary" }]);
    await waitFor(() => expect(within(vaultsSection).queryByText("Diary")).not.toBeInTheDocument());
    expect(useEditor.getState().currentPath).toBeNull();
    expect(useEditor.getState().recentPaths).toEqual([]);
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
