// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { minutesLeft } from "./DiscreetPill";
import { useDiscreet } from "./state";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
});

const vault = (name: string, discreet = false) => ({ name, notes: 0, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet });

function server(revealed: boolean, until: string | null) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed, until } }),
    "GET /api/vaults": () => json(200, revealed ? [vault("Work"), vault("Tagebuch", true)] : [vault("Work")]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
    "POST /api/account/discreet/hide": () => noContent(),
  });
}

/** Desktop-wide: the tree docked beside the note. */
function desktop() {
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", addEventListener() {}, removeEventListener() {} }));
}

const frameOf = (box: HTMLElement) => box.querySelector(":scope > [data-discreet-frame]");
const pill = () => screen.queryByRole("group", { name: "Discreet vaults shown" });

describe("the discreet pill", () => {
  it("rounds the time left up to whole minutes", () => {
    const now = Date.parse("2026-11-09T10:00:00Z");
    expect(minutesLeft("2026-11-09T10:09:00Z", now)).toBe(9);
    expect(minutesLeft("2026-11-09T10:08:30Z", now)).toBe(9);
    expect(minutesLeft("2026-11-09T10:00:20Z", now)).toBe(1);
  });

  it("shows the time left beside the docked sidebar's vault menu, in a frame, and counts down", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    desktop();
    server(true, new Date(Date.now() + 9 * 60_000).toISOString());
    renderApp("/");
    const sidebar = await screen.findByRole("complementary", { name: "File tree" });
    const shown = await within(sidebar).findByRole("group", { name: "Discreet vaults shown" });
    expect(shown).toHaveTextContent("Discreet · 9 min");
    expect(within(shown).getByRole("button", { name: "Hide discreet vaults" })).toBeInTheDocument();
    expect(frameOf(sidebar)).toBeInTheDocument();
    // Where the menu sits: just above the vault switcher.
    expect(shown.nextElementSibling).toContainElement(within(sidebar).getByRole("button", { name: /^Switch vault/ }));
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(shown).toHaveTextContent("Discreet · 8 min");
  });

  it("shows no time for Never", async () => {
    desktop();
    server(true, null);
    renderApp("/");
    const shown = await within(await screen.findByRole("complementary", { name: "File tree" })).findByRole("group", { name: "Discreet vaults shown" });
    expect(shown).toHaveTextContent(/^Discreet·$/);
    expect(shown).not.toHaveTextContent("min");
  });

  it("is not there, nor the frame, while they are hidden", async () => {
    desktop();
    server(false, null);
    renderApp("/");
    const sidebar = await screen.findByRole("complementary", { name: "File tree" });
    await within(sidebar).findByRole("button", { name: /^Switch vault/ });
    expect(pill()).not.toBeInTheDocument();
    expect(frameOf(sidebar)).toBeNull();
  });
});
