// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState, type ReactNode } from "react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RecoveryKeyReveal } from "../components/RecoveryKeyReveal";
import { SkillLinkReveal } from "../skills/Shared";
import { InWindow } from "../windows/context";
import { useWindows } from "../windows/store";
import WindowFrame from "../windows/WindowFrame";
import WindowNav from "../windows/WindowNav";
import ConnectorReveal from "./ConnectorReveal";
import DavConnect from "./DavConnect";
import InvitationReveal from "./admin/InvitationReveal";
import KeyReveal from "./KeyReveal";

const entries = [
  { to: "/w/one", label: "One", match: "/w/one" },
  { to: "/w/two", label: "Two", match: "/w/two" },
];
const connection = { id: "c1", name: "Work", url: "https://kumbu.example.org/mcp/c/Zk3", vaults: { Work: "write" } } as never;
const share = { id: "s1", name: "Files", username: "kumbu-ab12" } as never;

// A reveal as its page holds it: shown until its own Done.
function Page({ children }: { children: (done: () => void) => ReactNode }) {
  const [shown, setShown] = useState(true);
  return shown ? <>{children(() => setShown(false))}</> : <p>Nothing on show</p>;
}

const copy = async () => {
  await userEvent.click(screen.getAllByRole("button", { name: /^Copy/ })[0]);
};

const cases: { name: string; lock: string; reveal: (done: () => void) => ReactNode; finish: () => Promise<void> }[] = [
  {
    name: "a new key",
    lock: "Save the key first",
    reveal: (done) => <KeyReveal name="Claude" value="kumbu_secret" onDone={done} />,
    finish: async () => {
      await copy();
      await userEvent.click(screen.getByRole("button", { name: "Done" }));
    },
  },
  {
    name: "a connector",
    lock: "Copy the connector's details first",
    reveal: (done) => <ConnectorReveal connection={connection} onDone={done} />,
    finish: () => userEvent.click(screen.getByRole("button", { name: "Done" })),
  },
  {
    name: "a WebDAV password",
    lock: "Save the password first",
    reveal: (done) => <DavConnect share={share} password="p4ssw0rd" onDone={done} />,
    finish: async () => {
      await userEvent.click(screen.getByRole("button", { name: "Copy password" }));
      await userEvent.click(screen.getByRole("button", { name: "Done" }));
    },
  },
  {
    name: "a recovery key",
    lock: "Save the recovery key first",
    reveal: (done) => <RecoveryKeyReveal value="RK-1234" onContinue={done} />,
    finish: async () => {
      await copy();
      await userEvent.click(screen.getByRole("checkbox"));
      await userEvent.click(screen.getByRole("button", { name: "Continue" }));
    },
  },
  {
    name: "an invitation",
    lock: "Copy the invitation link first",
    reveal: (done) => <InvitationReveal link="https://kumbu.example/join#kumbu-inv_Zx" onDone={done} />,
    finish: async () => {
      await copy();
      await userEvent.click(screen.getByRole("button", { name: "Done" }));
    },
  },
  {
    name: "a skill's share link",
    lock: "Copy the share link first",
    reveal: (done) => <SkillLinkReveal name="review" link="https://kumbu.example/s/abc" onDone={done} />,
    finish: async () => {
      await copy();
      await userEvent.click(screen.getByRole("button", { name: "Done" }));
    },
  },
];

beforeEach(() => {
  useWindows.setState({ lock: null });
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
  Object.defineProperty(navigator, "clipboard", { value: { writeText: vi.fn(async () => {}) }, configurable: true });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderInWindow(reveal: (done: () => void) => ReactNode, inWindow = true) {
  const frame = (
    <InWindow.Provider value={inWindow}>
      <WindowFrame kind="navigated" title="Test" nav={<WindowNav label="Test" entries={entries} />}>
        <Page>{reveal}</Page>
      </WindowFrame>
    </InWindow.Provider>
  );
  render(<RouterProvider router={createMemoryRouter([{ path: "/w/*", element: frame }], { initialEntries: ["/w/one"] })} />);
  return within(screen.getByRole("navigation", { name: "Test" }));
}

describe("every reveal locks the window's navigation", () => {
  it.each(cases)("$name", async ({ lock, reveal, finish }) => {
    const nav = renderInWindow(reveal);

    expect(nav.getByText(lock)).toBeInTheDocument();
    for (const link of nav.getAllByRole("link")) expect(link).toHaveAttribute("aria-disabled", "true");

    await finish();
    // An unsaved reveal's Done asks first; the copied ones go straight on.
    expect(screen.getByText("Nothing on show")).toBeInTheDocument();
    expect(nav.queryByText(lock)).not.toBeInTheDocument();
    for (const link of nav.getAllByRole("link")) expect(link).not.toHaveAttribute("aria-disabled");
  });

  it("the connect page from a share's row shows no secret and locks nothing", () => {
    const nav = renderInWindow((done) => <DavConnect share={share} onDone={done} />);
    expect(useWindows.getState().lock).toBeNull();
    for (const link of nav.getAllByRole("link")) expect(link).not.toHaveAttribute("aria-disabled");
  });

  it("outside a window a reveal sets no lock", () => {
    renderInWindow((done) => <KeyReveal name="Claude" value="kumbu_secret" onDone={done} />, false);
    expect(useWindows.getState().lock).toBeNull();
  });
});
