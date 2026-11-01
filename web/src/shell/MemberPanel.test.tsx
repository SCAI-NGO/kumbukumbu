// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { fakeApi, json, noContent } from "../test/app";
import MemberPanel from "./MemberPanel";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useEditor.getState().reset();
});

const garten = {
  name: "garten",
  notes: 2,
  embeddings: "remote",
  pending: false,
  canWrite: true,
  level: "write",
  owner: '"Alice"',
  ownerUsername: "alice",
  shared: true,
  membership: "m1",
};
const providers = [
  { name: "internal", label: "On this server", description: null, external: false, available: true, reason: null, default: true },
  { name: "remote", label: "Remote Embeddings", description: null, external: true, available: true, reason: null, default: false },
];

function panelApi(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false }, garten]),
    "GET /api/embeddings": () => json(200, providers),
    ...extra,
  });
}

function renderPanel(onClose = () => {}) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <MemberPanel vault="garten" onClose={onClose} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return screen.findByRole("dialog", { name: "garten" });
}

const sent = (fetch: ReturnType<typeof fakeApi>, method: string) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method);

describe("MemberPanel", () => {
  it("shows the owner with her username, the role and the embedding provider", async () => {
    panelApi();
    const dialog = await renderPanel();
    expect(dialog).toHaveTextContent('Shared by"Alice" (alice)');
    expect(dialog).toHaveTextContent("Your roleRead and write");
    await waitFor(() => expect(dialog).toHaveTextContent("Search by meaningRemote Embeddings"));
  });

  it("leaves after confirmation", async () => {
    const fetch = panelApi({ "POST /api/memberships/m1/leave": () => noContent() });
    useEditor.setState({ activeVault: "garten", recentPaths: ["/garten/plan.md", "/Work/a.md"] });
    const onClose = vi.fn();
    const dialog = await renderPanel(onClose);

    await userEvent.click(within(dialog).getByRole("button", { name: "Leave this vault" }));
    const confirm = screen.getByRole("alertdialog", { name: "Leave garten?" });
    expect(confirm).toHaveTextContent("lose access at once");
    expect(sent(fetch, "POST")).toHaveLength(0);
    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(sent(fetch, "POST")).toHaveLength(0);

    await userEvent.click(within(dialog).getByRole("button", { name: "Leave this vault" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Leave vault" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(new URL(sent(fetch, "POST")[0].url).pathname).toBe("/api/memberships/m1/leave");
    expect(useEditor.getState().recentPaths).toEqual(["/Work/a.md"]);
    expect(useEditor.getState().activeVault).toBeNull();
  });

  it("renames the alias", async () => {
    const fetch = panelApi({ "PUT /api/memberships/m1/alias": () => noContent() });
    useEditor.setState({ activeVault: "garten", recentPaths: ["/garten/plan.md"] });
    const onClose = vi.fn();
    const dialog = await renderPanel(onClose);

    const field = within(dialog).getByLabelText("Your name for it");
    expect(field).toHaveValue("garten");
    expect(within(dialog).getByRole("button", { name: "Rename" })).toBeDisabled();
    await userEvent.clear(field);
    await userEvent.type(field, "Beete");
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename" }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(await sent(fetch, "PUT")[0].json()).toEqual({ alias: "Beete" });
    expect(useEditor.getState().activeVault).toBe("Beete");
    expect(useEditor.getState().recentPaths).toEqual(["/Beete/plan.md"]);
  });

  it("keeps the old name and says why when the new one is taken", async () => {
    panelApi({ "PUT /api/memberships/m1/alias": () => json(409, { error: "You already have a vault named 'Work'." }) });
    const onClose = vi.fn();
    const dialog = await renderPanel(onClose);
    await userEvent.clear(within(dialog).getByLabelText("Your name for it"));
    await userEvent.type(within(dialog).getByLabelText("Your name for it"), "Work");
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("You already have a vault named 'Work'.");
    expect(onClose).not.toHaveBeenCalled();
  });
});
