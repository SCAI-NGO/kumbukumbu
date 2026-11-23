// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, renderApp, user } from "../../test/app";

function api(byok = false) {
  return fakeApi({
    "GET /api/account/embeddings-key": () => json(200, { offered: byok, keySaved: false, last4: null, refusedAt: null }),
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    "GET /api/keys": () => json(200, []),
    "GET /api/connections": () => json(200, []),
    "GET /api/dav-shares": () => json(200, { enabled: true, shares: [] }),
    "GET /api/skills": () => json(200, { skills: [], withoutSkills: [] }),
    "GET /api/skills/marketplaces": () => json(200, { enabled: false, marketplaces: [] }),
    "POST /api/skills/updates/check": () => json(200, []),
  });
}

beforeEach(() => {
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const settings = () => screen.findByRole("dialog", { name: "Settings" });

describe("the settings window", () => {
  it("lists the nine entries in order", async () => {
    api();
    renderApp("/settings/appearance");
    const nav = within(await settings()).getByRole("navigation", { name: "Settings" });
    expect(within(nav).getAllByRole("link").map((l) => l.textContent)).toEqual(["Appearance", "Vaults", "Published", "Access", "Skills", "Calendar", "Security", "Your data", "About"]);
    expect(within(nav).getByRole("link", { name: "Access" })).toHaveAttribute("href", "/settings/access/keys");
    expect(within(nav).getByRole("link", { name: "Skills" })).toHaveAttribute("href", "/settings/skills/installed");
    expect(within(nav).getByRole("link", { name: "Appearance" })).toHaveAttribute("aria-current", "page");
  });

  it("adds External, with Embeddings on it, where the server offers a key of one's own", async () => {
    api(true);
    renderApp("/settings/external");
    const window = await settings();
    const nav = within(window).getByRole("navigation", { name: "Settings" });
    expect(await within(nav).findByRole("link", { name: "External" })).toHaveAttribute("aria-current", "page");
    expect(within(nav).getAllByRole("link").map((l) => l.textContent)).toEqual(["Appearance", "Vaults", "Published", "Access", "Skills", "Calendar", "External", "Security", "Your data", "About"]);
    expect(await within(window).findByRole("region", { name: "Embeddings" })).toBeInTheDocument();
  });

  it("each entry shows its sections only", async () => {
    api();
    renderApp("/settings/security");
    const window = await settings();
    for (const name of ["Password", "Discreet vaults", "Recovery key", "Re-encrypt my vaults", "Sessions"]) expect(await within(window).findByRole("region", { name })).toBeInTheDocument();
    expect(within(window).queryByRole("region", { name: "Colour scheme" })).not.toBeInTheDocument();
    expect(within(window).queryByRole("region", { name: "Vaults" })).not.toBeInTheDocument();
    expect(within(within(window).getByRole("navigation", { name: "Settings" })).getByRole("link", { name: "Security" })).toHaveAttribute("aria-current", "page");
  });

  it.each([
    ["Access", "/settings/access/keys", "Apps", "/settings/access/apps"],
    ["Skills", "/settings/skills/installed", "Try", "/settings/skills/try"],
  ])("%s tabs replace inside the window", async (list, from, name, to) => {
    api();
    const router = renderApp(["/", from]);
    const window = await settings();
    await userEvent.click(within(await within(window).findByRole("tablist", { name: list })).getByRole("tab", { name }));
    await waitFor(() => expect(router.state.location.pathname).toBe(to));
    expect(router.state.historyAction).toBe("REPLACE");

    await act(() => router.navigate(-1));
    expect(router.state.location.pathname).toBe("/");
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Settings" })).not.toBeInTheDocument());
  });
});
