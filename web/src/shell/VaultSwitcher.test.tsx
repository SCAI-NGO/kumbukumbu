// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { fakeApi, json } from "../test/app";
import VaultSwitcher from "./VaultSwitcher";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useEditor.getState().reset();
});

const own = (name: string) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false });
const shared = (name: string, owner: string, ownerUsername: string) => ({
  ...own(name),
  level: "write",
  owner,
  ownerUsername,
  shared: true,
  membership: `m-${name}`,
});
const longName = "Gartenarbeit im Frühjahr, im Sommer, im Herbst und im Winter, mit allen Beeten und Bäumen";

/** With the vaults read already, as in the drawer, which has read them for its tree by then. */
function renderSwitcher(vaults: unknown[] = [own("Work"), own("Gartenarbeit"), shared("garten", '"Alice"', "alice"), shared("Beete", '"Alice"', "alice"), shared("Rezepte", '"Carol"', "carol")]) {
  fakeApi({ "GET /api/vaults": () => json(200, vaults) });
  const onNewVault = vi.fn();
  const onRename = vi.fn();
  const router = createMemoryRouter([{ path: "*", element: <VaultSwitcher onNewVault={onNewVault} onRename={onRename} /> }], { initialEntries: ["/notes/Work/a.md"] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(["vaults"], vaults);
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { router, onNewVault, onRename };
}

const popout = () => screen.getByRole("dialog", { name: "Vaults" });
const labels = (list: HTMLElement) => within(list).getAllByRole("button").map((b) => b.textContent);

describe("VaultSwitcher", () => {
  it("the switcher is a button with the current vault's name", async () => {
    useEditor.setState({ activeVault: "Work" });
    renderSwitcher();
    const button = await screen.findByRole("button", { name: "Switch vault: Work" });
    expect(button).toHaveTextContent("Work");
    expect(button).toHaveAttribute("aria-haspopup", "dialog");
    expect(button).toHaveAttribute("aria-expanded", "false");
    // No row of chips any more: the other vaults are in the popout only.
    expect(screen.queryByRole("group", { name: "Switch vault" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Gartenarbeit" })).not.toBeInTheDocument();
  });

  it("its popout lists your vaults, then those shared with you by owner, and New vault", async () => {
    useEditor.setState({ activeVault: "Work" });
    const { onNewVault } = renderSwitcher();
    const button = await screen.findByRole("button", { name: "Switch vault: Work" });
    await userEvent.click(button);
    expect(button).toHaveAttribute("aria-expanded", "true");
    const dialog = popout();
    expect(labels(within(dialog).getByRole("list", { name: "Your vaults" }))).toEqual(["Work", "Gartenarbeit"]);
    expect(within(dialog).getByRole("heading", { name: "Shared with me" })).toBeInTheDocument();
    expect(labels(within(dialog).getByRole("list", { name: 'Shared by "Alice" (alice)' }))).toEqual(["garten", "Beete"]);
    expect(labels(within(dialog).getByRole("list", { name: 'Shared by "Carol" (carol)' }))).toEqual(["Rezepte"]);
    // Her own first, then each owner's, then the ways to a new vault and to all of them.
    expect(labels(dialog)).toEqual(["Work", "Gartenarbeit", "garten", "Beete", "Rezepte", "New vault", "All vaults"]);
    // The current one is marked, and has the focus.
    const current = within(dialog).getByRole("button", { name: "Work" });
    expect(current).toHaveAttribute("aria-current", "true");
    expect(current).toHaveFocus();
    expect(within(dialog).getByRole("button", { name: "Gartenarbeit" })).not.toHaveAttribute("aria-current");

    await userEvent.click(within(dialog).getByRole("button", { name: "New vault" }));
    expect(onNewVault).toHaveBeenCalled();
    expect(screen.queryByRole("dialog", { name: "Vaults" })).not.toBeInTheDocument();
  });

  it("choosing one switches and closes", async () => {
    useEditor.setState({ activeVault: "Work" });
    renderSwitcher();
    await userEvent.click(await screen.findByRole("button", { name: "Switch vault: Work" }));
    await userEvent.click(within(popout()).getByRole("button", { name: "Beete" }));
    expect(useEditor.getState().activeVault).toBe("Beete");
    expect(screen.queryByRole("dialog", { name: "Vaults" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Switch vault: Beete" })).toHaveFocus();
  });

  it("Escape closes and returns the focus", async () => {
    useEditor.setState({ activeVault: "Work" });
    renderSwitcher();
    const button = await screen.findByRole("button", { name: "Switch vault: Work" });
    await userEvent.click(button);
    await userEvent.tab();
    expect(popout()).toContainElement(document.activeElement as HTMLElement);
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Vaults" })).not.toBeInTheDocument();
    expect(button).toHaveFocus();
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(useEditor.getState().activeVault).toBe("Work");

    // A tap beside it closes it too.
    await userEvent.click(button);
    await userEvent.click(screen.getByTestId("vault-popout-scrim"));
    expect(screen.queryByRole("dialog", { name: "Vaults" })).not.toBeInTheDocument();
    expect(button).toHaveFocus();
  });

  it("a long name doesn't overflow a narrow sidebar", async () => {
    useEditor.setState({ activeVault: longName });
    renderSwitcher([own(longName), own("Work")]);
    const button = await screen.findByRole("button", { name: `Switch vault: ${longName}` });
    // The button takes the row's width and no more; its label is cut off with an ellipsis.
    expect(button).toHaveClass("w-full", "min-w-0");
    expect(within(button).getByText(longName)).toHaveClass("truncate");
    await userEvent.click(button);
    const entry = within(popout()).getByRole("button", { name: longName });
    expect(entry).toHaveClass("w-full");
    expect(within(entry).getByText(longName)).toHaveClass("truncate");
  });

  it("All vaults leads to the overview, and closes the drawer", async () => {
    useEditor.setState({ activeVault: "Work", overlay: "tree" });
    const { router } = renderSwitcher();
    await userEvent.click(await screen.findByRole("button", { name: "Switch vault: Work" }));
    await userEvent.click(within(popout()).getByRole("button", { name: "All vaults" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(router.state.location.state).toEqual({ overview: true });
    expect(useEditor.getState().overlay).toBe("none");
  });

  it("offers her own name for the current vault only when it is shared with her", async () => {
    useEditor.setState({ activeVault: "Work" });
    const { onRename } = renderSwitcher();
    await userEvent.click(await screen.findByRole("button", { name: "Switch vault: Work" }));
    expect(within(popout()).queryByRole("button", { name: "Your name for this vault…" })).not.toBeInTheDocument();
    await userEvent.click(within(popout()).getByRole("button", { name: "garten" }));

    await userEvent.click(screen.getByRole("button", { name: "Switch vault: garten" }));
    await userEvent.click(within(popout()).getByRole("button", { name: "Your name for this vault…" }));
    expect(onRename).toHaveBeenCalled();
    expect(screen.queryByRole("dialog", { name: "Vaults" })).not.toBeInTheDocument();
  });
});
