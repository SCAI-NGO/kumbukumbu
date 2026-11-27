// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline mode in the app (offline mode design §3a): what the API client tells it, the slot token it sends, and
// the bar's lines.
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { CopyHeader, SealedMirror } from "eidolon-core/store/mirror";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, test, vi } from "vitest";
import { api } from "../api/client";
import { useSession } from "../state/session";
import { fakeApi, json, user } from "../test/app";
import { useOfflineCopy } from "./copy";
import { conflictSentence, offlineSentence } from "./OfflineBar";
import OfflineBar from "./OfflineBar";
import { keepSessionThroughWipe, resetOfflineMode, useOfflineMode, WIPED } from "./mode";

const header = { copy: "c1", slot: { id: "s1", token: "tok-1" } } as CopyHeader;

afterEach(() => {
  vi.unstubAllGlobals();
  useOfflineCopy.getState().clear();
  useSession.getState().reset();
  resetOfflineMode();
});

describe("the API client", () => {
  test("a request that gets no answer goes offline; an answer from a gateway with Kumbu² down too", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))));
    await api.GET("/api/version").catch(() => {});
    expect(useOfflineMode.getState().offline).toBe(true);
  });

  test("a 503 from the gateway counts as unreachable", async () => {
    fakeApi({ "GET /api/version": () => new Response("down", { status: 503 }) });
    await api.GET("/api/version");
    expect(useOfflineMode.getState().offline).toBe(true);
  });

  test("a 503 of Kumbu²'s own, busy for a moment, reaches it", async () => {
    const busy = { "Content-Type": "application/json", "X-Kumbu-Version": "1.0.0" };
    fakeApi({ "GET /api/version": () => new Response(JSON.stringify({ error: "The server is busy; try again in a moment." }), { status: 503, headers: busy }) });
    await api.GET("/api/version");
    expect(useOfflineMode.getState().offline).toBe(false);
  });

  test("an ordinary failure of the app's own is no reason", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("a bug"))));
    await api.GET("/api/version").catch(() => {});
    expect(useOfflineMode.getState().offline).toBe(false);
  });

  test("sends the copy's slot token in Kumbu-Mirror", async () => {
    const fetch = fakeApi({ "GET /api/version": () => json(200, {}) });
    await api.GET("/api/version");
    expect(fetch.mock.calls[0]![0].headers.get("Kumbu-Mirror")).toBeNull();
    useOfflineCopy.getState().set(header, null);
    await api.GET("/api/version");
    expect(fetch.mock.calls[1]![0].headers.get("Kumbu-Mirror")).toBe("tok-1");
  });

  test("a 401 saying mirror-wipe removes the copy and goes to sign-in with the reason", async () => {
    const lock = vi.fn();
    // Nobody signed in yet: the copy's token goes with the request.
    useOfflineCopy.getState().set({ ...header }, { lock } as unknown as SealedMirror);
    fakeApi({ "GET /api/vaults": () => json(401, { error: "Gone.", code: "mirror-wipe" }) });
    // No IndexedDB here: only the tab's part of the wipe is looked at.
    vi.stubGlobal("indexedDB", undefined);
    await api.GET("/api/vaults").catch(() => {});
    await vi.waitFor(() => expect(useSession.getState().ended).toBe(true));
    expect(lock).toHaveBeenCalled();
    expect(useOfflineCopy.getState().header).toBeNull();
    expect(useSession.getState().notice).toBe(WIPED);
  });

  test("a 401 saying mirror-wipe to a request sent with another copy's token leaves the tab's copy and session", async () => {
    useOfflineCopy.getState().set({ ...header }, { lock: vi.fn() } as unknown as SealedMirror);
    const next = { ...header, copy: "copy-2", slot: { id: "s2", token: "tok-2" } };
    // The tab holds another copy by the time the answer comes.
    const fetch = fakeApi({ "GET /api/vaults": () => (useOfflineCopy.getState().set(next, null), json(401, { error: "Gone.", code: "mirror-wipe" })) });
    vi.stubGlobal("indexedDB", undefined);
    await api.GET("/api/vaults").catch(() => {});
    await act(() => new Promise((r) => setTimeout(r, 100)));
    expect(fetch.mock.calls[0]![0].headers.get("Kumbu-Mirror")).toBe("tok-1");
    expect(useOfflineCopy.getState().header).toEqual(next);
    expect(useSession.getState().ended).toBe(false);
  });

  test("a 401 saying mirror-wipe ends the session even while a re-encryption's recovery key is shown", async () => {
    // Nobody signed in, as in the test above: the copy's token goes with the request.
    useOfflineCopy.getState().set({ ...header }, { lock: vi.fn() } as unknown as SealedMirror);
    fakeApi({ "GET /api/vaults": () => json(401, { error: "Gone.", code: "mirror-wipe" }) });
    vi.stubGlobal("indexedDB", undefined);
    const release = keepSessionThroughWipe();
    try {
      await api.GET("/api/vaults").catch(() => {});
      await vi.waitFor(() => expect(useSession.getState().ended).toBe(true));
      expect(useSession.getState().user).toBeNull();
      expect(useSession.getState().notice).toBe(WIPED);
    } finally {
      release();
    }
  });

  test("a 401 while uploading changes keeps the place and asks to sign in over the app", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    useOfflineCopy.getState().set(header, { locked: false, waiting: () => 2 } as unknown as SealedMirror);
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))));
    await api.GET("/api/version").catch(() => {});
    useOfflineMode.getState().waitingChanged();
    fakeApi({ "GET /api/vaults": () => json(401, { error: "Signed out." }) });
    await api.GET("/api/vaults");
    expect(useSession.getState().ended).toBe(false);
    expect(useOfflineMode.getState()).toMatchObject({ offline: true, phase: "signIn", waiting: 2 });
  });
});

describe("the bar's sentences", () => {
  test.each([
    ["offline", 0, true, "You're offline. Your notes are here; what needs a connection is marked."],
    ["offline", 3, true, "You're offline. 3 changes will upload when you're back."],
    ["offline", 1, true, "You're offline. 1 change will upload when you're back."],
    ["offline", 0, false, "You're offline. This device keeps no copy of your notes."],
    ["uploading", 3, true, "Back online. Uploading 3 changes…"],
    ["signIn", 3, true, "Sign in to upload your 3 changes"],
  ] as const)("%s, %i waiting, copy %s", (phase, waiting, copy, sentence) => {
    expect(offlineSentence(phase, waiting, copy)).toBe(sentence);
  });

  test("conflicts", () => {
    expect(conflictSentence(4, 1)).toBe("Uploaded 4 changes. One note was also changed elsewhere: your version is kept beside it.");
    expect(conflictSentence(null, 3)).toBe("3 notes were also changed elsewhere.");
    expect(conflictSentence(2, 1, { vault: "v1", original: "Plan.md", copy: "Plan.md", came: "trashed-here" }))
      .toBe("Uploaded 2 changes. “Plan” came back: it was changed elsewhere after you trashed it.");
  });
});

/** The bar as the pages outside the shell show it, or, `inShell`, as the signed-in shell does. */
function renderBar(inShell = false) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter>
        <OfflineBar inShell={inShell} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("the bar", () => {
  test("offline it leads to Settings, Offline, and can't be put away", async () => {
    useOfflineMode.getState().failed();
    renderBar();
    const bar = await screen.findByRole("status", { name: "Offline" });
    expect(screen.getByRole("link", { name: /You're offline/ })).toHaveAttribute("href", "/settings/offline");
    expect(bar).not.toHaveTextContent("Dismiss");
  });

  test("the done line goes after four seconds", async () => {
    vi.useFakeTimers();
    try {
      useOfflineMode.setState({ offline: false, phase: "online", done: { uploaded: 2 }, conflicts: [] });
      renderBar();
      expect(screen.getByRole("status", { name: "Uploaded" })).toHaveTextContent("All changes uploaded");
      await act(() => vi.advanceTimersByTimeAsync(4_000));
      expect(screen.queryByRole("status", { name: "Uploaded" })).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  test("conflicts stay until dismissed", async () => {
    useOfflineMode.getState().conflict({ vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" });
    renderBar(true);
    expect(await screen.findByRole("status", { name: "Changed elsewhere" })).toHaveTextContent("One note was also changed elsewhere");
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByRole("status", { name: "Changed elsewhere" })).toBeNull();
  });

  test("conflicts are listed only inside the signed-in shell", async () => {
    useOfflineMode.getState().conflict({ vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" });
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toHaveLength(1));
    renderBar();
    expect(screen.queryByRole("status", { name: "Changed elsewhere" })).toBeNull();
  });

  test.each([
    ["trashed-here", "“Plan” came back: it was changed elsewhere after you trashed it."],
    ["trashed-elsewhere", "“Plan” was trashed elsewhere; it's back with your changes."],
  ] as const)("shows the came-back notice with Open, %s", async (came, sentence) => {
    useOfflineMode.getState().conflict({ vault: "v1", original: "projects/Plan.md", copy: "projects/Plan.md", came });
    renderBar(true);
    const line = await screen.findByRole("status", { name: "Changed elsewhere" });
    expect(line).toHaveTextContent(sentence);
    expect(screen.getByRole("button", { name: "Open" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Open" }));
    expect(screen.queryByRole("status", { name: "Changed elsewhere" })).toBeNull();
  });

  test("a purge names no vault", async () => {
    useOfflineMode.setState({ purged: { lost: 2 } });
    renderBar();
    expect(screen.getByRole("status", { name: "Offline copy" })).toHaveTextContent(
      "A vault's copy was removed from this device: it's no longer available here, with 2 changes that hadn't uploaded.",
    );
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByRole("status", { name: "Offline copy" })).toBeNull();
  });
});

describe("what the mode keeps of a person", () => {
  const alice = { ...user, id: "u1", username: "alice", display: "Alice" };
  const bob = { ...user, id: "u2", username: "bob", display: "Bob" };

  /** Two conflict copies and three changes waiting, from Alice's session. */
  async function alicesConflicts() {
    useSession.getState().signedIn({ user: alice, previousSignInAt: null });
    useOfflineMode.getState().conflict({ vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" });
    useOfflineMode.getState().conflict({ vault: "v1", original: "Letters/To Carol.md", copy: "Letters/To Carol (conflict 2026-11-22 21-05).md" });
    useOfflineMode.setState({ waiting: 3 });
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toHaveLength(2));
  }

  test("a sign-out forgets the conflicts listed for the previous person", async () => {
    await alicesConflicts();
    useSession.getState().signOut();
    await vi.waitFor(() => expect(useOfflineMode.getState()).toMatchObject({ conflicts: [], waiting: 0, done: null }));
  });

  test("a session's end forgets them too", async () => {
    await alicesConflicts();
    useSession.getState().end();
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toEqual([]));
  });

  test("someone else signing in forgets them", async () => {
    await alicesConflicts();
    useSession.getState().signedIn({ user: bob, previousSignInAt: null });
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toEqual([]));
  });

  test("the same person's offline sign-in, once the session is read, keeps them", async () => {
    useSession.getState().signedIn({ user: { ...alice, id: "" }, previousSignInAt: null });
    useOfflineMode.getState().conflict({ vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" });
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toHaveLength(1));
    useSession.getState().signedIn({ user: alice, previousSignInAt: null });
    await new Promise((r) => setTimeout(r, 20));
    expect(useOfflineMode.getState().conflicts).toHaveLength(1);
  });

  test("the copy locking forgets them", async () => {
    useOfflineCopy.getState().set(header, { header, locked: false } as unknown as SealedMirror);
    useOfflineMode.getState().conflict({ vault: "v1", original: "Plan.md", copy: "Plan (conflict 2026-11-22 21-04).md" });
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toHaveLength(1));
    useOfflineCopy.getState().set(header, null);
    await vi.waitFor(() => expect(useOfflineMode.getState().conflicts).toEqual([]));
  });
});
