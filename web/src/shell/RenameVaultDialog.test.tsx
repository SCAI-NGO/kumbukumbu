// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { fakeApi, json, noContent } from "../test/app";
import RenameVaultDialog from "./RenameVaultDialog";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useEditor.getState().reset();
});

const access = (deviceKeys: string[]) => json(200, { apiKeys: [], deviceKeys, connections: [], members: [], left: [] });

function dialogApi(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({ "GET /api/vaults/Gartenarbeit/access": () => access([]), ...extra });
}

function renderDialog({ at = "/", onClose = () => {}, queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }) } = {}) {
  const element = <RenameVaultDialog vault="Gartenarbeit" onClose={onClose} />;
  const router = createMemoryRouter(
    [
      { path: "/", element },
      { path: "/notes/*", element },
    ],
    { initialEntries: [at] },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { router, dialog: screen.findByRole("dialog", { name: "Rename Gartenarbeit" }) };
}

const sent = (fetch: ReturnType<typeof fakeApi>, method: string) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method);
const device = "Connected Obsidian devices need to pair again.";

async function rename(dialog: HTMLElement, name: string) {
  const field = within(dialog).getByLabelText("New name");
  await userEvent.clear(field);
  await userEvent.type(field, name);
  const button = within(dialog).getByRole("button", { name: "Rename vault" });
  await waitFor(() => expect(button).toBeEnabled());
  await userEvent.click(button);
}

describe("RenameVaultDialog", () => {
  it("says what breaks, before the name is changed", async () => {
    const fetch = dialogApi();
    const dialog = await renderDialog().dialog;
    expect(within(dialog).getByRole("heading")).toHaveTextContent("Rename Gartenarbeit");
    expect(dialog).toHaveTextContent("Members keep their own name for it.");
    expect(dialog).toHaveTextContent("Agents find it under the new name; paths with the old one stop working.");
    expect(within(dialog).getByLabelText("New name")).toHaveValue("Gartenarbeit");
    expect(within(dialog).getByRole("button", { name: "Cancel" })).toBeInTheDocument();
    expect(sent(fetch, "PUT")).toEqual([]);
  });

  it("names Obsidian devices only if there are device keys", async () => {
    dialogApi({ "GET /api/vaults/Gartenarbeit/access": () => access(["Gartenarbeit – Obsidian Laptop"]) });
    const dialog = await renderDialog().dialog;
    await waitFor(() => expect(dialog).toHaveTextContent(device));
    // The sentence names no device.
    expect(dialog).not.toHaveTextContent("Obsidian Laptop");
    cleanup();

    dialogApi();
    const none = await renderDialog().dialog;
    await waitFor(() => expect(within(none).getByRole("button", { name: "Rename vault" })).toBeDisabled());
    await userEvent.type(within(none).getByLabelText("New name"), "x");
    await waitFor(() => expect(within(none).getByRole("button", { name: "Rename vault" })).toBeEnabled());
    expect(none).not.toHaveTextContent(device);
  });

  it("says it also if the list can't be read", async () => {
    dialogApi({ "GET /api/vaults/Gartenarbeit/access": () => json(503, { error: "The server is busy." }) });
    const dialog = await renderDialog().dialog;
    await waitFor(() => expect(dialog).toHaveTextContent(device));
  });

  it("sends the name, normalised and trimmed", async () => {
    const fetch = dialogApi({ "PUT /api/vaults/Gartenarbeit/name": () => noContent() });
    const onClose = vi.fn();
    const dialog = await renderDialog({ onClose }).dialog;
    await rename(dialog, " Garten ");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const [put] = sent(fetch, "PUT");
    expect(new URL(put.url).pathname).toBe("/api/vaults/Gartenarbeit/name");
    expect(await put.json()).toEqual({ name: "Garten" });
  });

  it("goes on showing the same note under the new name", async () => {
    dialogApi({ "PUT /api/vaults/Gartenarbeit/name": () => noContent() });
    useEditor.setState({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md", recentPaths: ["/Gartenarbeit/plan.md", "/Work/a.md"] });
    const onClose = vi.fn();
    const { router, dialog } = renderDialog({ at: "/notes/Gartenarbeit/plan.md", onClose });
    await rename(await dialog, "Garten");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(router.state.location.pathname).toBe("/notes/Garten/plan.md");
    expect(useEditor.getState().activeVault).toBe("Garten");
    expect(useEditor.getState().currentPath).toBe("/Garten/plan.md");
    expect(useEditor.getState().recentPaths).toEqual(["/Garten/plan.md", "/Work/a.md"]);
  });

  it("on a page with no note open, only the active vault changes", async () => {
    dialogApi({ "PUT /api/vaults/Gartenarbeit/name": () => noContent() });
    useEditor.setState({ activeVault: "Gartenarbeit" });
    const onClose = vi.fn();
    const { router, dialog } = renderDialog({ onClose });
    await rename(await dialog, "Garten");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(useEditor.getState().activeVault).toBe("Garten");
    expect(router.state.location.pathname).toBe("/");
  });

  it("forgets the old name's caches and asks for the vaults again", async () => {
    dialogApi({ "PUT /api/vaults/Gartenarbeit/name": () => noContent() });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["tree", "Gartenarbeit"], { damaged: 0 });
    queryClient.setQueryData(["note", "/Gartenarbeit/plan.md"], { content: "Text." });
    queryClient.setQueryData(["tree", "Work"], { damaged: 0 });
    queryClient.setQueryData(["vaults"], []);
    const onClose = vi.fn();
    await rename(await renderDialog({ onClose, queryClient }).dialog, "Garten");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(queryClient.getQueryData(["tree", "Gartenarbeit"])).toBeUndefined();
    expect(queryClient.getQueryData(["note", "/Gartenarbeit/plan.md"])).toBeUndefined();
    expect(queryClient.getQueryData(["tree", "Work"])).toBeDefined();
    expect(queryClient.getQueryState(["vaults"])?.isInvalidated).toBe(true);
  });

  it("shows a taken name and stays open", async () => {
    dialogApi({ "PUT /api/vaults/Gartenarbeit/name": () => json(409, { error: "You already have a vault named 'Work'." }) });
    useEditor.setState({ activeVault: "Gartenarbeit", currentPath: "/Gartenarbeit/plan.md", recentPaths: ["/Gartenarbeit/plan.md"] });
    const onClose = vi.fn();
    const { router, dialog } = renderDialog({ at: "/notes/Gartenarbeit/plan.md", onClose });
    const shown = await dialog;
    await rename(shown, "Work");
    expect(await within(shown).findByRole("alert")).toHaveTextContent("You already have a vault named 'Work'.");
    expect(onClose).not.toHaveBeenCalled();
    expect(router.state.location.pathname).toBe("/notes/Gartenarbeit/plan.md");
    expect(useEditor.getState().activeVault).toBe("Gartenarbeit");
    expect(useEditor.getState().recentPaths).toEqual(["/Gartenarbeit/plan.md"]);
  });

  it("offers no Rename while the name is unchanged", async () => {
    dialogApi();
    const dialog = await renderDialog().dialog;
    const field = within(dialog).getByLabelText("New name");
    const button = within(dialog).getByRole("button", { name: "Rename vault" });
    // A case-only change is one: enabled once the access list is in, so the disabled ones below aren't held by it.
    await userEvent.clear(field);
    await userEvent.type(field, "gartenarbeit");
    await waitFor(() => expect(button).toBeEnabled());
    await userEvent.clear(field);
    expect(button).toBeDisabled();
    await userEvent.type(field, "   ");
    expect(button).toBeDisabled();
    await userEvent.clear(field);
    await userEvent.type(field, " Gartenarbeit ");
    expect(button).toBeDisabled();
  });
});
