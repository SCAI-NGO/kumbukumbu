// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, renderApp, user } from "../../test/app";

function api() {
  return fakeApi({
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

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  sessionStorage.clear();
});

describe("the old addresses", () => {
  it.each([
    ["/settings", "/settings/appearance"],
    ["/settings#install", "/settings/appearance#install"],
    ["/settings#discreet-vaults", "/settings/security#discreet-vaults"],
    ["/access", "/settings/access/keys"],
    ["/keys", "/settings/access/keys"],
    ["/access/apps", "/settings/access/apps"],
    ["/skills", "/settings/skills/installed"],
    ["/skills/try", "/settings/skills/try"],
  ])("%s goes to %s, in its place", async (from, to) => {
    api();
    const router = renderApp(["/", from]);
    await waitFor(() => expect(router.state.location.pathname + router.state.location.hash).toBe(to));
    expect(router.state.historyAction).toBe("REPLACE");
    expect(await screen.findByRole("dialog", { name: "Settings" })).toBeInTheDocument();
  });

  it("/skills/add still adds a shared skill, before the app", async () => {
    fakeApi({ "GET /api/session": () => json(401, { error: "Sign in first." }) });
    const router = renderApp("/skills/add#ABCD-EFGH-JKLM-NPQR-STUV-WXYZ");
    expect(await screen.findByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/signin?next=%2Fskills%2Fadd");
    expect(router.state.location.pathname).toBe("/skills/add");
    expect(screen.queryByRole("dialog", { name: "Settings" })).not.toBeInTheDocument();
  });
});
