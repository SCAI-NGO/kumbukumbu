// SPDX-License-Identifier: AGPL-3.0-or-later
// The signed-in part of the app when reading the session fails (F-16, hand tests of 0.7.0): a failed read never takes
// the place of an app that has its session, the session is read again once the offline mode is over, and where there
// is no session yet the sentence comes with a way to try again.
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { resetOfflineMode, useOfflineMode } from "../offline/mode";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const unreachable = "The server can't be reached right now. Try again in a moment.";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  resetOfflineMode();
});

/** The API of a signed-in shell; the session's answer is what `session()` says at each read. */
function shellApi(session: () => Response | Promise<Response>) {
  return fakeApi({
    "GET /api/session": () => session(),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 0 }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
  });
}

const sessionReads = (fetch: ReturnType<typeof fakeApi>) =>
  fetch.mock.calls.filter(([r]) => new URL((r as Request).url).pathname === "/api/session").length;

describe("RequireSession", () => {
  it("keeps the app when a later read of the session fails", async () => {
    let up = true;
    shellApi(() => (up ? json(200, { user, previousSignInAt: null }) : Promise.reject(new TypeError("Failed to fetch"))));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/", queryClient);
    await screen.findByText("Your vaults");

    up = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(queryClient.getQueryState(["session"])?.status).toBe("error"));
    expect(screen.queryByText(unreachable)).toBeNull();
    expect(screen.getByText("Your vaults")).toBeTruthy();
  });

  it("reads the session again when the offline mode ends", async () => {
    const fetch = shellApi(() => json(200, { user, previousSignInAt: null }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/", queryClient);
    await screen.findByText("Your vaults");
    act(() => useOfflineMode.setState({ offline: true }));
    const before = sessionReads(fetch);

    act(() => useOfflineMode.setState({ offline: false }));
    await waitFor(() => expect(sessionReads(fetch)).toBeGreaterThan(before));
  });

  // A network error makes the app offline, where the sign-in form opens the copy instead; a fault of the server doesn't.
  it("without a session, a fault of the server says so with Try again, which reads it again", async () => {
    let up = false;
    shellApi(() => (up ? json(200, { user, previousSignInAt: null }) : json(500, {})));
    renderApp("/");
    await screen.findByText(unreachable);

    up = true;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    await screen.findByText("Your vaults");
  });
});
