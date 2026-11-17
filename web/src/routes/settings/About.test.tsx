// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, renderApp, user } from "../../test/app";
import { useUpdate } from "../../update/store";

const version = vi.hoisted(() => ({ app: "0.2.0+a6362ec" }));
vi.mock("../../update/version", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../../update/version")>();
  return {
    ...actual,
    get appVersion() {
      return version.app;
    },
  };
});

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

describe("Settings: About", () => {
  it("shows the app's version, and the server's when it differs", async () => {
    api();
    renderApp("/settings/about");
    const about = await screen.findByRole("region", { name: "About" });
    expect(about).toHaveTextContent("Kumbu² 0.2.0 (a6362ec)");
    expect(about).not.toHaveTextContent("Server:");

    act(() => useUpdate.getState().report("0.2.1+b0b0b0b"));
    expect(about).toHaveTextContent("Server: 0.2.1 (b0b0b0b)");
    act(() => useUpdate.setState({ server: null, available: null }));
  });

  it("calls a local build a development build", async () => {
    version.app = "0.0.0-dev";
    try {
      api();
      renderApp("/settings/about");
      const about = await screen.findByRole("region", { name: "About" });
      expect(about).toHaveTextContent("Kumbu² development build");
      expect(about).not.toHaveTextContent("0.0.0");
    } finally {
      version.app = "0.2.0+a6362ec";
    }
  });
});
