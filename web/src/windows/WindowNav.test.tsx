// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { InWindow, useWindowLock } from "./context";
import { useWindows } from "./store";
import WindowFrame from "./WindowFrame";
import WindowNav from "./WindowNav";

let desktop = true;
beforeEach(() => {
  desktop = true;
  useWindows.setState({ lock: null });
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      return query === "(min-width: 64rem)" ? desktop : false;
    },
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const entries = [
  { to: "/settings/one", label: "One", match: "/settings/one" },
  { to: "/settings/two", label: "Two", match: "/settings/two" },
  // An entry that opens on one of its tabs, and stays current on the others.
  { to: "/settings/three/a", label: "Three", match: "/settings/three" },
];

function Locker({ text }: { text: string }) {
  useWindowLock(text);
  return null;
}

function renderWindow(locker?: boolean, at = "/settings/one") {
  const page = (
    <InWindow.Provider value>
      <WindowFrame kind="navigated" title="Settings" nav={<WindowNav label="Settings" entries={entries} />}>
        <button type="button">Content control</button>
        {locker && <Locker text="Save the key first" />}
      </WindowFrame>
    </InWindow.Provider>
  );
  const router = createMemoryRouter([{ path: "/settings/*", element: page }, { path: "*", element: null }], { initialEntries: [at] });
  render(<RouterProvider router={router} />);
  return router;
}

describe("WindowNav", () => {
  it("marks the current entry and replaces on a move", async () => {
    const router = renderWindow();
    expect(screen.getByRole("link", { name: "One" })).toHaveAttribute("aria-current", "page");
    await userEvent.click(screen.getByRole("link", { name: "Two" }));
    expect(screen.getByRole("link", { name: "Two" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "One" })).not.toHaveAttribute("aria-current");
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("marks an entry current on any page under its prefix, not only the one it links to", () => {
    renderWindow(false, "/settings/three/b");
    expect(screen.getByRole("link", { name: "Three" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "One" })).not.toHaveAttribute("aria-current");
  });

  it("opens with the focus on the current entry, not on ✕", () => {
    renderWindow(false, "/settings/two");
    expect(screen.getByRole("link", { name: "Two" })).toHaveFocus();
  });

  it("keeps Tab inside the pane and the title bar while the content under the pane is out of reach", async () => {
    desktop = false;
    renderWindow();
    const tab = screen.getByRole("button", { name: "Show navigation" });
    await userEvent.click(tab);
    expect(tab).toHaveFocus();
    // The tab is the last control within reach: on from it is the window's first, ✕.
    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Close" })).toHaveFocus();
    await userEvent.tab({ shift: true });
    expect(tab).toHaveFocus();
  });

  it("the phone tab opens and folds the pane", async () => {
    desktop = false;
    const router = renderWindow();
    const tab = screen.getByRole("button", { name: "Show navigation" });
    expect(tab).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("link", { name: "Two" })).not.toBeInTheDocument();

    await userEvent.click(tab);
    expect(tab).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: "Content control", hidden: true }).closest("[inert]")).not.toBeNull();
    expect(screen.getByRole("navigation", { name: "Settings" }).parentElement).toHaveClass("motion-safe:animate-[k-slide-right_200ms_ease-out]");
    await userEvent.click(screen.getByRole("link", { name: "Two" }));
    expect(tab).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByRole("button", { name: "Content control" }).closest("[inert]")).toBeNull();
    expect(router.state.location.pathname).toBe("/settings/two");

    await userEvent.click(tab);
    await userEvent.keyboard("{Escape}");
    expect(tab).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByRole("dialog", { name: "Settings" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/settings/two");

    await userEvent.click(tab);
    await userEvent.click(screen.getByTestId("nav-scrim"));
    expect(tab).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByRole("dialog", { name: "Settings" })).toBeInTheDocument();
  });

  it("a lock disables the entries and the tab", async () => {
    const router = renderWindow(true);
    expect(screen.getByText("Save the key first")).toBeInTheDocument();
    const two = screen.getByRole("link", { name: "Two" });
    expect(two).toHaveAttribute("aria-disabled", "true");
    await userEvent.click(two);
    expect(router.state.location.pathname).toBe("/settings/one");
    cleanup();

    desktop = false;
    renderWindow(true);
    expect(screen.getByRole("button", { name: "Show navigation" })).toBeDisabled();
    cleanup();
    expect(useWindows.getState().lock).toBeNull();
  });

  it("one lock ending leaves another's in place", () => {
    const a = render(<InWindow.Provider value><Locker text="A" /></InWindow.Provider>);
    expect(useWindows.getState().lock).toBe("A");
    render(<InWindow.Provider value><Locker text="B" /></InWindow.Provider>);
    expect(useWindows.getState().lock).toBe("B");
    a.unmount();
    expect(useWindows.getState().lock).toBe("B");
  });

  it("a lock outside a window does nothing", () => {
    render(<Locker text="x" />);
    expect(useWindows.getState().lock).toBeNull();
  });
});
