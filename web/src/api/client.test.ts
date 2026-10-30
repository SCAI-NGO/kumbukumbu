// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, user } from "../test/app";
import { usePasswordPrompt } from "../state/passwordPrompt";
import { useSession } from "../state/session";
import { api, call, HttpError } from "./client";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  usePasswordPrompt.getState().settle(false);
  useSession.getState().reset();
});

const passwordRequired = () => json(401, { error: "Give your password again to go on.", type: "password-required" });
const createKey = () => call(api.POST("/api/keys", { body: { name: "Laptop", vaults: { Work: "read" } } }));
const created = { key: "kumbu_x" };

/** Waits for the password dialog, then answers it. */
async function answerPrompt(confirmed: boolean) {
  await vi.waitFor(() => expect(usePasswordPrompt.getState().open).toBe(true));
  usePasswordPrompt.getState().settle(confirmed);
}

describe("api client", () => {
  it("sends same-origin requests with the cookie", async () => {
    const fetch = fakeApi({ "GET /api/session": () => json(200, { user, previousSignInAt: null }) });
    await api.GET("/api/session");
    const request = fetch.mock.calls[0][0];
    expect(new URL(request.url).origin).toBe(window.location.origin);
    expect(request.credentials).toBe("include");
  });

  it("encodes each segment of a note path and keeps the slashes between them", async () => {
    const fetch = fakeApi({ "GET /api/notes/My%20Vault/a%20b/c%25%3F.md": () => json(404, { error: "No." }) });
    await api.GET("/api/notes/{vault}/{path}", { params: { path: { vault: "My Vault", path: "a b/c%?.md" } } });
    expect(new URL(fetch.mock.calls[0][0].url).pathname).toBe("/api/notes/My%20Vault/a%20b/c%25%3F.md");
  });

  it("encodes each segment of an upload's folder, and leaves an empty folder (the vault's root) out", async () => {
    const fetch = fakeApi({ "POST /api/upload/*": () => json(400, { error: "No." }) });
    const body = { files: [{ path: "a.md", content: "a" }], onConflict: "ask" };
    await api.POST("/api/upload/{vault}/{folder}", { params: { path: { vault: "My Vault", folder: "a b/c%" } }, body });
    await api.POST("/api/upload/{vault}/{folder}", { params: { path: { vault: "My Vault", folder: "" } }, body });
    expect(fetch.mock.calls.map((c) => new URL(c[0].url).pathname)).toEqual(["/api/upload/My%20Vault/a%20b/c%25", "/api/upload/My%20Vault"]);
  });

  it("keeps a slash in any other parameter encoded", async () => {
    const fetch = fakeApi({ "GET /api/vaults/a%2Fb/tree": () => json(404, { error: "No." }) });
    await api.GET("/api/vaults/{vault}/tree", { params: { path: { vault: "a/b" } } });
    expect(new URL(fetch.mock.calls[0][0].url).pathname).toBe("/api/vaults/a%2Fb/tree");
  });

  it("ends the session on a 401 from any signed-in request, with a message if one was running", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    fakeApi({ "GET /api/keys": () => json(401, { error: "Sign in first." }) });
    await api.GET("/api/keys");
    const state = useSession.getState();
    expect(state.user).toBeNull();
    expect(state.ended).toBe(true);
    expect(state.notice).toBe("Your session has ended. Please sign in again.");
  });

  it("says nothing on a 401 when nobody was signed in", async () => {
    fakeApi({ "GET /api/session": () => json(401, { error: "Sign in first." }) });
    await api.GET("/api/session");
    expect(useSession.getState().ended).toBe(true);
    expect(useSession.getState().notice).toBeNull();
  });

  it("takes a 401 after signing out for the sign-out, except for the session itself", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    useSession.getState().signOut();
    fakeApi({ "GET /api/notes/*": () => json(401, { error: "Sign in first." }), "GET /api/session": () => json(401, { error: "Sign in first." }) });
    await api.GET("/api/notes/{vault}/{path}", { params: { path: { vault: "W", path: "a.md" } } });
    expect(useSession.getState().ended).toBe(false);
    await api.GET("/api/session");
    expect(useSession.getState().ended).toBe(true);
    // Signing in again ends that.
    useSession.getState().signedIn({ user, previousSignInAt: null });
    expect(useSession.getState().signedOut).toBe(false);
  });

  it.each([
    ["POST /api/session", () => api.POST("/api/session", { body: { username: "ada", password: "x", remember: false } })],
    ["POST /api/join", () => api.POST("/api/join", { body: { code: "c", username: "ada", password: "x" } })],
    ["POST /api/recover", () => api.POST("/api/recover", { body: { username: "ada", recoveryKey: "k", newPassword: "x" } })],
    ["POST /api/invitations/peek", () => api.POST("/api/invitations/peek", { body: { code: "c" } })],
  ])("leaves the session alone on a 401 from %s itself", async (key, send) => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    fakeApi({ [key]: () => json(401, { error: "The username or password is wrong." }) });
    await send();
    expect(useSession.getState().ended).toBe(false);
    expect(useSession.getState().user).toEqual(user);
  });

  it("keeps the session when the password dialog is cancelled", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    fakeApi({ "POST /api/keys": passwordRequired });
    const result = createKey().catch((e) => e);
    await answerPrompt(false);
    const error = await result;
    expect(error).toBeInstanceOf(HttpError);
    expect(error.status).toBe(401);
    expect(useSession.getState().user).toEqual(user);
    expect(useSession.getState().ended).toBe(false);
  });

  it("repeats a guarded request once after the password is confirmed", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    const answers = [passwordRequired, () => json(201, created)];
    const fetch = fakeApi({ "POST /api/keys": () => answers.shift()!() });
    const result = createKey();
    await answerPrompt(true);
    expect(await result).toEqual(created);
    const bodies = await Promise.all(fetch.mock.calls.map((c) => c[0].text()));
    expect(bodies).toHaveLength(2);
    expect(bodies[1]).toBe(bodies[0]);
    expect(JSON.parse(bodies[1])).toEqual({ name: "Laptop", vaults: { Work: "read" } });
    expect(useSession.getState().ended).toBe(false);
  });

  it("keeps no copy of an upload's body to repeat: uploads are never guarded", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    fakeApi({ "PUT /api/files/*": () => json(201, {}), "POST /api/upload/*": () => json(200, {}) });
    const clone = vi.spyOn(Request.prototype, "clone");
    const form = new FormData();
    form.append("file", new Blob(["%PDF-1.4"]), "scan.pdf");
    await api.POST("/api/upload/{vault}/{folder}", { params: { path: { vault: "Work", folder: "" } }, body: form as never });
    await api.PUT("/api/files/{vault}/{path}", {
      params: { path: { vault: "Work", path: "scan.pdf" } },
      body: new Uint8Array([1, 2, 3]) as unknown as string,
      bodySerializer: (body: unknown) => body as BodyInit,
      headers: { "Content-Type": "application/octet-stream" },
    });
    expect(clone).not.toHaveBeenCalled();
  });

  it("does not ask a second time when the repeated request is refused again", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    const ask = vi.spyOn(usePasswordPrompt.getState(), "ask");
    const fetch = fakeApi({ "POST /api/keys": passwordRequired });
    const result = createKey().catch((e) => e);
    await answerPrompt(true);
    const error = await result;
    expect(error).toBeInstanceOf(HttpError);
    expect(error.status).toBe(401);
    expect(ask).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(usePasswordPrompt.getState().open).toBe(false);
    expect(useSession.getState().ended).toBe(false);
  });

  it("asks once for two guarded requests and repeats both", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    const ask = vi.spyOn(usePasswordPrompt.getState(), "ask");
    let opened = 0;
    const unsubscribe = usePasswordPrompt.subscribe((s, prev) => {
      if (s.open && !prev.open) opened++;
    });
    let confirmed = false;
    const fetch = fakeApi({ "POST /api/keys": () => (confirmed ? json(201, created) : passwordRequired()) });
    const first = createKey();
    const second = createKey();
    await vi.waitFor(() => expect(ask).toHaveBeenCalledTimes(2));
    confirmed = true;
    usePasswordPrompt.getState().settle(true);
    expect(await Promise.all([first, second])).toEqual([created, created]);
    unsubscribe();
    expect(opened).toBe(1);
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it("still ends the session on a plain 401", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    const fetch = fakeApi({ "POST /api/keys": () => json(401, { error: "Sign in first." }) });
    await createKey().catch(() => {});
    expect(usePasswordPrompt.getState().open).toBe(false);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(useSession.getState().user).toBeNull();
    expect(useSession.getState().ended).toBe(true);
  });

  it("call() returns the data or throws the server's sentence with the status", async () => {
    fakeApi({ "POST /api/session": () => json(429, { error: "Too many attempts; try again in a minute." }) });
    const error = await call(api.POST("/api/session", { body: { username: "ada", password: "x", remember: false } })).catch((e) => e);
    expect(error).toBeInstanceOf(HttpError);
    expect(error.status).toBe(429);
    expect(error.message).toBe("Too many attempts; try again in a minute.");
  });
});
