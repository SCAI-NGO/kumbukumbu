// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { safeNext } from "./SignIn";

const view = {
  requestId: "r1",
  client: { name: "Claude · claude.ai", host: "claude.ai", verified: true },
  redirectHost: "claude.ai",
  connection: { name: "cor.energy", vaults: { Arbeit: "write", Privat: "read" }, expiresAt: null as string | null, currentClient: null },
  renewal: false,
  problem: null as string | null,
};
const redirect = "https://claude.ai/api/mcp/auth_callback?code=kumbu_ac_secret&state=s";
const expired = "This request has expired; start the connection again in the app.";

const path = (router: ReturnType<typeof renderApp>) => router.state.location.pathname + router.state.location.search;

const assign = vi.fn();

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/oauth/requests/r1": () => json(200, view),
    "POST /api/oauth/requests/r1/decision": () => json(200, { redirect }),
    ...overrides,
  });
}

const decisions = (fetch: ReturnType<typeof api>) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.url.endsWith("/decision"));

beforeEach(() => {
  assign.mockReset();
  vi.stubGlobal("location", { ...window.location, assign });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("Consent", () => {
  it("shows exactly what is being connected", async () => {
    api();
    renderApp("/consent?request=r1");
    expect(await screen.findByRole("heading", { name: "Connect Claude · claude.ai?" })).toBeInTheDocument();
    expect(screen.getByText(/wants to use your connection/)).toHaveTextContent("Claude · claude.ai (claude.ai) wants to use your connection cor.energy:");
    expect(screen.getByText("Arbeit: read and write")).toBeInTheDocument();
    expect(screen.getByText("Privat: read")).toBeInTheDocument();
    expect(screen.getByText("It returns to claude.ai afterwards.")).toBeInTheDocument();
    expect(screen.queryByText(/registered itself/)).not.toBeInTheDocument();
    expect(screen.queryByText(/^Expires/)).not.toBeInTheDocument();
  });

  it("warns about an app that registered itself and shows the expiry", async () => {
    api({ "GET /api/oauth/requests/r1": () => json(200, { ...view, client: { ...view.client, verified: false }, connection: { ...view.connection, expiresAt: "2026-12-01T09:00:00Z" } }) });
    renderApp("/consent?request=r1");
    expect(await screen.findByText("This app registered itself; Kumbu² can't confirm who made it.")).toBeInTheDocument();
    expect(screen.getByText("Expires 1 Dec 2026")).toBeInTheDocument();
  });

  it("says when it renews a connection", async () => {
    api({ "GET /api/oauth/requests/r1": () => json(200, { ...view, renewal: true }) });
    renderApp("/consent?request=r1");
    expect(await screen.findByRole("heading", { name: "Renew cor.energy for Claude · claude.ai?" })).toBeInTheDocument();
    expect(screen.getByText("This renews the connection with the same vaults and rights.")).toBeInTheDocument();
  });

  it("allow navigates to the redirect", async () => {
    const fetch = api();
    renderApp("/consent?request=r1");
    await userEvent.click(await screen.findByRole("button", { name: "Allow" }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith(redirect));
    const [post] = decisions(fetch);
    expect(await post.clone().json()).toEqual({ allow: true });
    // No second decision while the browser leaves.
    expect(screen.getByRole("button", { name: "Allow" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Deny" })).toBeDisabled();
  });

  it("deny navigates with the denial", async () => {
    const fetch = api();
    renderApp("/consent?request=r1");
    await userEvent.click(await screen.findByRole("button", { name: "Deny" }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith(redirect));
    expect(await decisions(fetch)[0].clone().json()).toEqual({ allow: false });
  });

  it("shows why a decision failed and lets the user try again", async () => {
    api({ "POST /api/oauth/requests/r1/decision": () => json(404, { error: expired }) });
    renderApp("/consent?request=r1");
    await userEvent.click(await screen.findByRole("button", { name: "Allow" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(expired);
    expect(assign).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Allow" })).toBeEnabled();
  });

  it("Signs_in_first_and_comes_back_to_the_same_request", async () => {
    let signedIn = false;
    api({
      "GET /api/session": () => (signedIn ? json(200, { user, previousSignInAt: null }) : json(401, { error: "Sign in first." })),
      "POST /api/session": () => {
        signedIn = true;
        return json(200, { user, previousSignInAt: null });
      },
    });
    const router = renderApp("/consent?request=r1");
    await screen.findByRole("button", { name: "Sign in" });
    expect(path(router)).toBe("/signin?next=%2Fconsent%3Frequest%3Dr1");
    expect(safeNext("/consent?request=r1")).toBe("/consent?request=r1");
    await userEvent.type(screen.getByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("button", { name: "Allow" })).toBeInTheDocument();
    expect(path(router)).toBe("/consent?request=r1");
  });

  it.each([expired, "The app could not be identified any more; start the connection again in the app."])(
    "An_expired_request_says_to_start_again: %s",
    async (message) => {
      api({ "GET /api/oauth/requests/r1": () => json(404, { error: message }) });
      renderApp("/consent?request=r1");
      expect(await screen.findByText(message)).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Allow" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Deny" })).not.toBeInTheDocument();
    },
  );

  it("the plain mcp address explains connections", async () => {
    const fetch = api({ "GET /api/oauth/requests/r1": () => json(200, { ...view, connection: null, problem: "not_a_connection" }) });
    renderApp("/consent?request=r1");
    expect(await screen.findByText("This address isn't a connection. In Kumbu², open Access → Connect an app, and use the connector URL it shows.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Allow" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Back to claude.ai" }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith(redirect));
    expect(await decisions(fetch)[0].clone().json()).toEqual({ allow: false });
  });

  it("another user's connection offers sign-out", async () => {
    let signedOut = false;
    const fetch = api({
      "GET /api/oauth/requests/r1": () => json(200, { ...view, problem: "other_user" }),
      "GET /api/session": () => (signedOut ? json(401, { error: "Sign in first." }) : json(200, { user, previousSignInAt: null })),
      "DELETE /api/session": () => {
        signedOut = true;
        return noContent();
      },
    });
    const router = renderApp("/consent?request=r1");
    expect(await screen.findByText("This connection belongs to another Kumbu² account. Sign out and sign in as its owner.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Allow" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back to claude.ai" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(path(router)).toBe("/signin?next=%2Fconsent%3Frequest%3Dr1"));
    expect(fetch.mock.calls.some(([r]) => (r as Request).method === "DELETE" && (r as Request).url.endsWith("/api/session"))).toBe(true);
  });

  it.each([
    ["revoked", "This connection was revoked. Make a new connection in Kumbu² and add its URL in the app."],
    ["expired", "This connection expired. Make a new connection in Kumbu² and add its URL in the app."],
  ])("a %s connection says so", async (problem, text) => {
    api({ "GET /api/oauth/requests/r1": () => json(200, { ...view, problem }) });
    renderApp("/consent?request=r1");
    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Allow" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back" })).toBeInTheDocument();
  });

  it.each([
    ["unknown_client", "The app could not be identified, so Kumbu² can't connect it."],
    ["invalid_redirect_uri", "The app asked to return to an address it didn't register. Nothing was connected."],
    ["server_error", "The app's request could not be used. Nothing was connected."],
  ])("errors before the app is trusted stay here: %s", async (code, text) => {
    const fetch = fakeApi({});
    renderApp(`/consent?error=${code}`);
    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("keeps nothing in browser storage", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    api();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/consent?request=r1", queryClient);
    await userEvent.click(await screen.findByRole("button", { name: "Allow" }));
    await waitFor(() => expect(assign).toHaveBeenCalled());
    expect(setItem).not.toHaveBeenCalled();
    expect(Object.keys(localStorage)).toEqual([]);
    expect(Object.keys(sessionStorage)).toEqual([]);
    // Neither the view nor the decision (it holds the code) outlives the page: both have gcTime 0.
    cleanup();
    await waitFor(() => {
      expect(queryClient.getQueryCache().findAll({ queryKey: ["consent"] })).toEqual([]);
      expect(queryClient.getMutationCache().getAll()).toEqual([]);
    });
  });
});
