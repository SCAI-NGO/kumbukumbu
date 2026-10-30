// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, user } from "../test/app";
import { useSession } from "../state/session";
import { api, call, HttpError } from "./client";

afterEach(() => {
  vi.unstubAllGlobals();
  useSession.getState().reset();
});

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

  it("call() returns the data or throws the server's sentence with the status", async () => {
    fakeApi({ "POST /api/session": () => json(429, { error: "Too many attempts; try again in a minute." }) });
    const error = await call(api.POST("/api/session", { body: { username: "ada", password: "x", remember: false } })).catch((e) => e);
    expect(error).toBeInstanceOf(HttpError);
    expect(error.status).toBe(429);
    expect(error.message).toBe("Too many attempts; try again in a minute.");
  });
});
