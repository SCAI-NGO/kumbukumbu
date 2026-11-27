// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, onTestFinished, vi } from "vitest";
import { useDiscreet } from "../../discreet/state";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, navigateAway, noContent, renderApp, user } from "../../test/app";

const contact = vi.hoisted(() => vi.fn(async () => {}));
vi.mock("../../offline/mode", async (original) => ({ ...(await original<typeof import("../../offline/mode")>()), contact }));

const recoveryKey = "WXYZ-2345-6789-ABCD";

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

describe("Settings: Security", () => {
  it("changes the password, says every session ends, and goes to sign-in", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/password": async (r) => (bodies.push(await r.json()), json(200, { signedOut: true })) });
    const router = renderApp("/settings/security");
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
    const router = renderApp("/settings/security");
    const form = await screen.findByRole("region", { name: "Password" });
    const revoke = within(form).getByLabelText("Also revoke all API keys and WebDAV shares, and disconnect apps");
    expect(revoke).toBeChecked();
    expect(form).toHaveTextContent("Agents using them will need new keys, drives mounted with a share stop working, and apps will need to reconnect.");

    await userEvent.click(revoke);
    await userEvent.type(within(form).getByLabelText("Current password"), "old password here");
    await userEvent.type(within(form).getByLabelText("New password"), "a much longer new one");
    await userEvent.click(within(form).getByRole("button", { name: "Change password" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(bodies).toEqual([{ oldPassword: "old password here", newPassword: "a much longer new one", revokeApiKeys: false }]);
  });

  it("shows the server's sentence when the password change is refused", async () => {
    api({ "POST /api/password": () => json(400, { error: "The current password is wrong." }) });
    renderApp("/settings/security");
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
    const router = renderApp("/settings/security", queryClient);
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
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe("/settings/security");
    expect(JSON.stringify({ ...localStorage })).not.toContain(recoveryKey);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(recoveryKey));
  });

  it("asks once before leaving with the new recovery key unsaved", async () => {
    api({ "POST /api/recovery-key": () => json(200, { recoveryKey }) });
    const router = renderApp("/settings/security");
    const part = await screen.findByRole("region", { name: "Recovery key" });
    await userEvent.type(within(part).getByLabelText("Current password"), "my password");
    await userEvent.click(within(part).getByRole("button", { name: "Make a new recovery key" }));
    await screen.findByText(recoveryKey);

    await navigateAway(router, "/settings/access/keys");
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved your recovery key");
    expect(router.state.location.pathname).toBe("/settings/security");
  });

  it("the re-encrypt section says to change a leaked password", async () => {
    api();
    renderApp("/settings/security");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    expect(part).toHaveTextContent(
      "New keys for your account and every vault. API keys, connected apps and WebDAV shares lose access and need to be set up or reconnected; you get a new recovery key. If you think your password leaked, change it too.",
    );
  });

  it("re-encrypting shows the new recovery key once", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    const bodies: unknown[] = [];
    api({ "POST /api/account/reencrypt": async (r) => (bodies.push(await r.json()), json(200, { recoveryKey })) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, "invalidateQueries");
    renderApp("/settings/security", queryClient);
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
    // The shares were revoked with the keys: the WebDAV tab must not show them as live.
    expect(invalidated).toHaveBeenCalledWith({ queryKey: ["dav-shares"] });
    expect(JSON.stringify({ ...localStorage })).not.toContain(recoveryKey);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(recoveryKey));
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data))).not.toContain(recoveryKey);
  });

  it("re-encrypting my vaults makes contact once it succeeds", async () => {
    contact.mockClear();
    api({ "POST /api/account/reencrypt": () => json(200, { recoveryKey }) });
    renderApp("/settings/security");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    await userEvent.type(within(part).getByLabelText("Current password"), "my password");
    await userEvent.click(within(part).getByRole("button", { name: "Re-encrypt my vaults" }));
    expect(await within(part).findByText(recoveryKey)).toBeInTheDocument();
    // The copy on this device learns at once that its slot went, while it is online and has nothing waiting.
    expect(contact).toHaveBeenCalledTimes(1);
  });

  it("shows the server's sentence when re-encrypting is refused", async () => {
    contact.mockClear();
    api({ "POST /api/account/reencrypt": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings/security");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    await userEvent.type(within(part).getByLabelText("Current password"), "wrong");
    await userEvent.click(within(part).getByRole("button", { name: "Re-encrypt my vaults" }));
    expect(await within(part).findByText("The password is wrong.")).toBeInTheDocument();
    expect(contact).not.toHaveBeenCalled();
  });

  it("says a re-encryption may have gone through when its answer is lost, and to make a new recovery key", async () => {
    api({ "POST /api/account/reencrypt": () => json(502, {}) });
    renderApp("/settings/security");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    await userEvent.type(within(part).getByLabelText("Current password"), "my password");
    await userEvent.click(within(part).getByRole("button", { name: "Re-encrypt my vaults" }));
    expect(await within(part).findByText(/may have gone through even so/)).toHaveTextContent('make a new recovery key under "Recovery key" above');
  });

  it("says nothing of that when re-encrypting was refused", async () => {
    api({ "POST /api/account/reencrypt": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings/security");
    const part = await screen.findByRole("region", { name: "Re-encrypt my vaults" });
    await userEvent.type(within(part).getByLabelText("Current password"), "wrong");
    await userEvent.click(within(part).getByRole("button", { name: "Re-encrypt my vaults" }));
    expect(await within(part).findByText("The password is wrong.")).toBeInTheDocument();
    expect(part).not.toHaveTextContent(/may have gone through/);
  });

  it("signs out the other sessions", async () => {
    let ended = 0;
    api({ "POST /api/sessions/end-others": () => (ended++, noContent()) });
    renderApp("/settings/security");
    await screen.findByRole("region", { name: "Sessions" });
    await userEvent.click(within(section("Sessions")).getByRole("button", { name: "Sign out other sessions" }));
    expect(await within(section("Sessions")).findByText(/Every other session has ended\./)).toBeInTheDocument();
    expect(ended).toBe(1);
  });

  it("End other sessions says every other device's copy stays", async () => {
    api({ "POST /api/sessions/end-others": () => noContent() });
    renderApp("/settings/security");
    await screen.findByRole("region", { name: "Sessions" });
    await userEvent.click(within(section("Sessions")).getByRole("button", { name: "Sign out other sessions" }));
    expect(await within(section("Sessions")).findByText(/Every other session has ended\./)).toHaveTextContent(
      "Every other device's offline copy stays until you remove it in Settings, Offline.",
    );
  });
});

describe("Settings: discreet vaults", () => {
  const discreetVaults = [
    { name: "Work", notes: 3, kind: "notes", discreet: false },
    { name: "Tagebuch", notes: 1, kind: "notes", discreet: true },
    { name: "Skills", notes: 2, kind: "skills", discreet: false },
  ];
  const session = (revealed: boolean) => () => json(200, { user, previousSignInAt: null, discreet: { revealed, until: null } });

  afterEach(() => useDiscreet.getState().reset());

  it("looks the same for everyone while hidden: Set or change PIN, no switches", async () => {
    // Whatever the server holds, the hidden session's answers are the same: so is the section.
    const texts: string[] = [];
    for (const vaults of [[{ name: "Work", notes: 3 }], [{ name: "Work", notes: 3 }, { name: "Garten", notes: 1 }]]) {
      api({ "GET /api/session": session(false), "GET /api/vaults": () => json(200, vaults) });
      renderApp("/settings/security");
      const region = await screen.findByRole("region", { name: "Discreet vaults" });
      // The vault list behind the window is in: the switches would be there by now.
      await within(await screen.findByRole("list", { name: "Your vaults" })).findByRole("button", { name: /^Work/ });
      expect(within(region).getByRole("button", { name: "Set or change PIN" })).toBeInTheDocument();
      expect(within(region).queryAllByRole("switch")).toEqual([]);
      texts.push(region.textContent ?? "");
      cleanup();
    }
    expect(texts[0]).toBe(texts[1]);
    expect(texts[0]).not.toContain("Garten");
  });

  it("has an id to link to", async () => {
    api({ "GET /api/session": session(false) });
    renderApp("/settings/security");
    expect(await screen.findByRole("region", { name: "Discreet vaults" })).toHaveAttribute("id", "discreet-vaults");
  });

  it("is scrolled to when a link leads there", async () => {
    const scrolled: Element[] = [];
    // jsdom has no scrollIntoView.
    Element.prototype.scrollIntoView = function (this: Element) {
      scrolled.push(this);
    };
    onTestFinished(() => void delete (Element.prototype as Partial<Element>).scrollIntoView);
    api({ "GET /api/session": session(false) });
    renderApp("/settings/security#discreet-vaults");
    const region = await screen.findByRole("region", { name: "Discreet vaults" });
    await waitFor(() => expect(scrolled.some((e) => e.contains(region))).toBe(true));
  });

  it("comes after the password", async () => {
    api({ "GET /api/session": session(false) });
    renderApp("/settings/security");
    await screen.findByRole("region", { name: "Discreet vaults" });
    const names = screen.getAllByRole("region").map((r) => r.getAttribute("aria-labelledby") && document.getElementById(r.getAttribute("aria-labelledby")!)?.textContent);
    expect(names.indexOf("Discreet vaults")).toBe(names.indexOf("Password") + 1);
  });

  it("while shown, has a Discreet switch per vault, none for Skills, and marks one", async () => {
    const marks: unknown[] = [];
    api({
      "GET /api/session": session(true),
      "GET /api/vaults": () => json(200, discreetVaults),
      "PUT /api/account/discreet/vaults/Work": async (r) => (marks.push(await r.json()), noContent()),
    });
    renderApp("/settings/security");
    const region = await screen.findByRole("region", { name: "Discreet vaults" });
    const switches = await within(region).findAllByRole("switch");
    expect(switches.map((s) => s.getAttribute("aria-label"))).toEqual(["Discreet: Work", "Discreet: Tagebuch"]);
    expect(within(region).getByRole("switch", { name: "Discreet: Tagebuch" })).toBeChecked();
    expect(within(region).getByRole("switch", { name: "Discreet: Work" })).not.toBeChecked();
    await userEvent.click(within(region).getByRole("switch", { name: "Discreet: Work" }));
    await waitFor(() => expect(marks).toEqual([{ discreet: true }]));
  });

  it("sets the PIN with the current password, and says so the same way every time", async () => {
    const bodies: unknown[] = [];
    api({ "GET /api/session": session(false), "POST /api/account/discreet/pin": async (r) => (bodies.push(await r.json()), noContent()) });
    renderApp("/settings/security");
    const region = await screen.findByRole("region", { name: "Discreet vaults" });
    await userEvent.click(within(region).getByRole("button", { name: "Set or change PIN" }));
    const dialog = screen.getByRole("dialog", { name: "Set or change PIN" });
    const pin = within(dialog).getByLabelText("New PIN");
    // Masked, but no password field: a password manager would offer to store the PIN as the account's password.
    for (const field of [pin, within(dialog).getByLabelText("New PIN again")]) {
      expect(field).toHaveAttribute("type", "text");
      expect(field).toHaveClass("k-pin");
      expect(field).toHaveAttribute("inputmode", "numeric");
      expect(field).toHaveAttribute("autocomplete", "one-time-code");
    }
    expect(pin).toHaveAccessibleDescription("4 to 6 digits");
    await userEvent.type(within(dialog).getByLabelText("Current password"), "my password");
    await userEvent.type(pin, "4711");
    await userEvent.type(within(dialog).getByLabelText("New PIN again"), "4712");
    const passwords = [...dialog.querySelectorAll<HTMLInputElement>('input[type="password"]')];
    expect(passwords.map((p) => p.value)).toEqual(["my password"]);
    await userEvent.click(within(dialog).getByRole("button", { name: "Set PIN" }));
    expect(within(dialog).getByRole("alert")).toHaveTextContent("The two PINs differ.");
    expect(within(dialog).getByLabelText("New PIN again")).toHaveAttribute("aria-invalid", "true");
    expect(bodies).toEqual([]);
    await userEvent.clear(within(dialog).getByLabelText("New PIN again"));
    await userEvent.type(within(dialog).getByLabelText("New PIN again"), "4711");
    expect(within(dialog).getByLabelText("New PIN again")).not.toHaveAttribute("aria-invalid", "true");
    await userEvent.click(within(dialog).getByRole("button", { name: "Set PIN" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Set or change PIN" })).not.toBeInTheDocument());
    expect(bodies).toEqual([{ password: "my password", pin: "4711" }]);
    expect(within(region).getByRole("status")).toHaveTextContent("The PIN is set.");
  });

  it("shows the server's sentence for a wrong password", async () => {
    api({ "GET /api/session": session(false), "POST /api/account/discreet/pin": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings/security");
    const region = await screen.findByRole("region", { name: "Discreet vaults" });
    await userEvent.click(within(region).getByRole("button", { name: "Set or change PIN" }));
    const dialog = screen.getByRole("dialog", { name: "Set or change PIN" });
    await userEvent.type(within(dialog).getByLabelText("Current password"), "wrong");
    await userEvent.type(within(dialog).getByLabelText("New PIN"), "4711");
    await userEvent.type(within(dialog).getByLabelText("New PIN again"), "4711");
    await userEvent.click(within(dialog).getByRole("button", { name: "Set PIN" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("The password is wrong.");
    // Another password: the old answer goes.
    await userEvent.type(within(dialog).getByLabelText("Current password"), "!");
    expect(within(dialog).queryByRole("alert")).not.toBeInTheDocument();
  });
});
