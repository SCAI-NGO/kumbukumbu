// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../../test/app";

const none = { offered: true, keySaved: false, last4: null, refusedAt: null };
const saved = { offered: true, keySaved: true, last4: "abcd", refusedAt: null };

function api(view: unknown, overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () =>
      json(200, [
        { name: "Work", notes: 3, embeddings: "internal", level: "owner" },
        { name: "Diary", notes: 1, embeddings: "internal", level: "owner" },
        { name: "Secret", notes: 1, embeddings: "internal", level: "owner", discreet: true },
        { name: "Family", notes: 1, embeddings: "internal", level: "read", owner: "bob" },
        { name: "Ideas", notes: 1, embeddings: "tensorx-byok", level: "owner" },
      ]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    "GET /api/account/embeddings-key": () => json(200, view),
    ...overrides,
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const section = () => screen.findByRole("region", { name: "Embeddings" });

describe("Settings, External: Embeddings with your own key", () => {
  it("isn't there when the server doesn't offer it, nor is the External entry", async () => {
    api({ ...none, offered: false });
    renderApp("/settings/external");
    expect(await screen.findByText("This server offers no external services.")).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Embeddings" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "External" })).not.toBeInTheDocument();
  });

  it("isn't on the Vaults page any more", async () => {
    api(none);
    renderApp("/settings/vaults");
    await screen.findByRole("region", { name: "Vaults" });
    expect(screen.queryByRole("region", { name: "Embeddings" })).not.toBeInTheDocument();
  });

  it("saves a key, then offers to move the person's own vaults, all ticked", async () => {
    let view: unknown = none;
    const moved: string[] = [];
    api(view, {
      "GET /api/account/embeddings-key": () => json(200, view),
      "PUT /api/account/embeddings-key": async (r) => {
        expect(await r.json()).toEqual({ key: "sk-test-key-abcd" });
        view = saved;
        return json(200, saved);
      },
      "PUT /api/vaults/*": async (r) => {
        expect(await r.json()).toEqual({ provider: "tensorx-byok" });
        moved.push(decodeURIComponent(new URL(r.url).pathname.split("/")[3]));
        return json(200, {});
      },
    });
    renderApp("/settings/external");
    const region = await section();
    expect(region).toHaveTextContent("TensorX");
    // With the spaces a paste may bring along: the key goes without them.
    await userEvent.type(within(region).getByLabelText("TensorX key"), "  sk-test-key-abcd ");
    await userEvent.click(within(region).getByRole("button", { name: "Save key" }));

    const prompt = await screen.findByRole("dialog", { name: "Move these 2 vaults to TensorX too?" });
    // Own vaults only, not discreet ones, not those already on TensorX.
    const boxes = within(prompt).getAllByRole("checkbox");
    expect(boxes.map((b) => b.getAttribute("name"))).toEqual(["Work", "Diary"]);
    boxes.forEach((b) => expect(b).toBeChecked());
    await userEvent.click(within(prompt).getByRole("checkbox", { name: "Diary" }));
    await userEvent.click(within(prompt).getByRole("button", { name: "Move" }));

    await waitFor(() => expect(screen.queryByRole("dialog", { name: /Move these/ })).not.toBeInTheDocument());
    expect(moved).toEqual(["Work"]);
    expect(await within(region).findByText("TensorX key: saved, ends in …abcd")).toBeInTheDocument();
  });

  it("moves nothing on Not now", async () => {
    const fetch = api(none, {
      "PUT /api/account/embeddings-key": () => json(200, saved),
    });
    renderApp("/settings/external");
    const region = await section();
    await userEvent.type(within(region).getByLabelText("TensorX key"), "sk-test-key-abcd");
    await userEvent.click(within(region).getByRole("button", { name: "Save key" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: /Move these/ })).getByRole("button", { name: "Not now" }));
    expect(fetch.mock.calls.some(([r]: [Request]) => r.method === "PUT" && r.url.includes("/api/vaults/"))).toBe(false);
  });

  it("shows the server's sentence for a refused key", async () => {
    api(none, { "PUT /api/account/embeddings-key": () => json(400, { error: "TensorX refused this key." }) });
    renderApp("/settings/external");
    const region = await section();
    await userEvent.type(within(region).getByLabelText("TensorX key"), "sk-wrong-key-0000");
    await userEvent.click(within(region).getByRole("button", { name: "Save key" }));
    expect(await within(region).findByRole("alert")).toHaveTextContent("TensorX refused this key.");
  });

  it("shows a saved key by its last four, says when TensorX refused it, and removes it", async () => {
    let view: unknown = { ...saved, refusedAt: "2026-11-18T08:00:00Z" };
    let removed = false;
    api(view, {
      "GET /api/account/embeddings-key": () => json(200, view),
      "DELETE /api/account/embeddings-key": () => {
        removed = true;
        view = none;
        return noContent();
      },
    });
    renderApp("/settings/external");
    const region = await section();
    expect(await within(region).findByText("TensorX key: saved, ends in …abcd")).toBeInTheDocument();
    expect(region).toHaveTextContent("TensorX refused the key");
    expect(within(region).getByRole("button", { name: "Replace key" })).toBeInTheDocument();

    await userEvent.click(within(region).getByRole("button", { name: "Remove key" }));
    expect(region).toHaveTextContent("go back to this server");
    await userEvent.click(within(region).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(removed).toBe(true));
    expect(await within(region).findByLabelText("TensorX key")).toBeInTheDocument();
  });
});
