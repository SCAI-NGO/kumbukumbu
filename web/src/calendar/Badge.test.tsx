// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { noteEntry, openTask, writtenView } from "../test/calendar";
import type { WhatsOnView } from "./api";
import Badge from "./Badge";

const setAppBadge = vi.fn(async (_count?: number) => {});
const clearAppBadge = vi.fn(async () => {});

/** The Badging API, as an installed app has it. */
function badging() {
  Object.defineProperty(navigator, "setAppBadge", { value: setAppBadge, configurable: true });
  Object.defineProperty(navigator, "clearAppBadge", { value: clearAppBadge, configurable: true });
}

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  setAppBadge.mockClear();
  clearAppBadge.mockClear();
  delete (navigator as { setAppBadge?: unknown }).setAppBadge;
  delete (navigator as { clearAppBadge?: unknown }).clearAppBadge;
  useSession.getState().reset();
  useEditor.getState().reset();
});

const count = (n: number, overdue: boolean, vaults: WhatsOnView["vaults"] = []): WhatsOnView => ({ items: [], count: n, overdue, vaults, couldntRead: 0, couldntReadVaults: [] });

const root = { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 10, kind: "note", mediaType: "text/markdown" }] };
const vault = (name: string) => ({ name, notes: 1, embeddings: "internal", canWrite: true, level: "owner", shared: false });

/** The shell on a note of Work, the badge answering `answer()` each time it is asked. */
function shell(answer: () => WhatsOnView, extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [vault("Work"), vault("Home")]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root }),
    "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }),
    "GET /api/notes/*": () => json(200, { path: "/Work/inbox.md", content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" }),
    "GET /api/version/*": () => json(200, { path: "/Work/inbox.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" }),
    "GET /api/vaults/Work/calendar/entry": () => json(200, { entry: null, couldntRead: null, canWrite: true }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
    "GET /api/calendar/whats-on": (r) => {
      expect(new URL(r.url).searchParams.get("count")).toBe("true");
      return json(200, answer());
    },
    ...extra,
  });
}

/** The badge saying `label`: its pill, around the number and the words a screen reader hears. */
const badge = (label: string) => screen.getByText(label).parentElement!;
const treeButton = () => screen.findByRole("button", { name: "Open file tree" });
const drawer = () => screen.getByRole("dialog", { name: "File tree" });
const asked = (fetch: ReturnType<typeof fakeApi>) => fetch.mock.calls.filter(([r]) => new URL((r as Request).url).pathname === "/api/calendar/whats-on").length;

describe("the badge", () => {
  it("the badge hides at zero and uses the warning colour when overdue", () => {
    const { container, rerender } = render(<Badge count={0} overdue={false} />);
    expect(container).toBeEmptyDOMElement();
    rerender(<Badge count={0} overdue />);
    expect(container).toBeEmptyDOMElement();

    rerender(<Badge count={3} overdue={false} />);
    const calm = badge("3 on");
    expect(calm).toHaveTextContent(/^3/);
    expect(calm.className).not.toMatch(/warning/);
    expect(calm).toHaveClass("bg-surface-2", "text-text");
    // Never by colour alone: overdue has its icon beside the number, and says so.
    expect(calm.querySelector("svg")).toBeNull();

    rerender(<Badge count={3} overdue />);
    const warning = badge("3 on, some overdue");
    expect(warning).toHaveClass("bg-warning", "text-bg");
    expect(warning.querySelector("svg")).not.toBeNull();
    expect(warning.className).not.toMatch(/red/);

    rerender(<Badge count={1} overdue />);
    expect(badge("1 on, overdue")).toBeInTheDocument();
  });

  it("the badge sits on What's on, the active vault's Calendar and each vault in the switcher", async () => {
    shell(() => count(3, true, [{ vault: "Work", count: 2, overdue: true }, { vault: "Home", count: 1, overdue: false }]));
    useEditor.setState({ activeVault: "Work" });
    renderApp("/notes/Work/inbox.md");
    // The phone's menu button carries the total, as its description: its name stays what it does.
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("3 on, some overdue"));
    // On the button's corner, within its box and the room it keeps beside it: never over where the note is.
    const pill = within(await treeButton()).getByText("3 on, some overdue").parentElement!;
    expect(pill).toHaveClass("absolute", "top-0", "right-0");
    expect(pill.className).not.toMatch(/-right-|-top-/);
    expect(await treeButton()).toHaveClass("mr-1.5");
    await userEvent.click(await treeButton());

    const account = within(drawer()).getByRole("navigation", { name: "Account" });
    const links = within(account).getAllByRole("link");
    expect(links[0]).toHaveAttribute("href", "/whats-on");
    expect(links[0]).toHaveTextContent(/^What's on/);
    expect(within(links[0]).getByText("3 on, some overdue")).toBeInTheDocument();

    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    expect(within(screen.getByRole("menuitem", { name: /^Calendar/ })).getByText("2 on, some overdue")).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");

    await userEvent.click(within(drawer()).getByRole("button", { name: "Switch vault: Work" }));
    const vaults = screen.getByRole("dialog", { name: "Vaults" });
    expect(within(within(vaults).getByRole("button", { name: /^Work/ })).getByText("2 on, some overdue")).toBeInTheDocument();
    expect(within(within(vaults).getByRole("button", { name: /^Home/ })).getByText("1 on")).toBeInTheDocument();
  });

  it("shows no badge anywhere at zero", async () => {
    const fetch = shell(() => count(0, false));
    renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(asked(fetch)).toBeGreaterThan(0));
    await userEvent.click(await treeButton());
    expect(within(drawer()).getByRole("link", { name: "What's on" })).toBeInTheDocument();
    expect(screen.queryByText(/^\d+ on/)).not.toBeInTheDocument();
    expect(await treeButton()).not.toHaveAccessibleDescription(/on/);
  });

  it("the app icon badge is set where supported", async () => {
    badging();
    let n = 3;
    shell(() => count(n, false));
    renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(3));
    // Nothing on: the icon is cleared, not set to zero.
    n = 0;
    await act(async () => void window.dispatchEvent(new Event("visibilitychange")));
    await waitFor(() => expect(clearAppBadge).toHaveBeenCalled());
    expect(setAppBadge).not.toHaveBeenCalledWith(0);
  });

  /** Home discreet in the list, the session showing it or not; What's on counting Work 1 and Home `home`. */
  const discreetHome = (revealed: boolean, home: () => number, vaults: () => Promise<Response> | Response = () => json(200, [vault("Work"), { ...vault("Home"), discreet: true }])) =>
    shell(() => count(1 + home(), false, [{ vault: "Work", count: 1, overdue: false }, ...(home() > 0 ? [{ vault: "Home", count: home(), overdue: false }] : [])]), {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed, until: null } }),
      "GET /api/vaults": vaults,
    });

  it("the app icon leaves discreet vaults out, shown or not", async () => {
    for (const revealed of [true, false]) {
      badging();
      discreetHome(revealed, () => 2);
      renderApp("/notes/Work/inbox.md");
      await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(1));
      expect(setAppBadge).not.toHaveBeenCalledWith(3);
      cleanup();
      useSession.getState().reset();
      setAppBadge.mockClear();
    }
  });

  it("the app icon is cleared when only discreet vaults have something on", async () => {
    badging();
    shell(() => count(2, false, [{ vault: "Home", count: 2, overdue: false }]), {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: true, until: null } }),
      "GET /api/vaults": () => json(200, [vault("Work"), { ...vault("Home"), discreet: true }]),
    });
    renderApp("/notes/Work/inbox.md");
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("2 on"));
    await waitFor(() => expect(clearAppBadge).toHaveBeenCalled());
    expect(setAppBadge).not.toHaveBeenCalled();
  });

  it("the in-app badge still counts what the session shows", async () => {
    badging();
    discreetHome(true, () => 2);
    renderApp("/notes/Work/inbox.md");
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("3 on"));
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(1));
  });

  it("badge before the vault list → no setAppBadge with the discreet share", async () => {
    badging();
    let listed = (_: Response) => {};
    const fetch = discreetHome(true, () => 2, () => new Promise<Response>((resolve) => (listed = resolve)));
    renderApp("/notes/Work/inbox.md");
    // What's on is in, the vault list isn't: the icon waits for it.
    await waitFor(() => expect(asked(fetch)).toBeGreaterThan(0));
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("3 on"));
    expect(setAppBadge).not.toHaveBeenCalled();
    expect(clearAppBadge).not.toHaveBeenCalled();
    await act(async () => listed(json(200, [vault("Work"), { ...vault("Home"), discreet: true }])));
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(1));
    expect(setAppBadge).not.toHaveBeenCalledWith(3);
  });

  it("reveal → the badge answering before the vault list → no setAppBadge with the discreet share", async () => {
    badging();
    let revealed = false;
    let listed = (_: Response) => {};
    shell(() => (revealed ? count(3, false, [{ vault: "Work", count: 1, overdue: false }, { vault: "Home", count: 2, overdue: false }]) : count(1, false, [{ vault: "Work", count: 1, overdue: false }])), {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed, until: null } }),
      // Hidden, the list leaves Home out; shown, it says Home is discreet, but only once it is let through.
      "GET /api/vaults": () => (revealed ? new Promise<Response>((resolve) => (listed = resolve)) : json(200, [vault("Work")])),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/notes/Work/inbox.md", queryClient);
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(1));

    // What showing them refreshes (discreet/api.ts), all at once.
    revealed = true;
    await act(async () => {
      for (const queryKey of [["session"], ["vaults"], ["whats-on"]]) void queryClient.invalidateQueries({ queryKey });
    });
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("3 on"));
    expect(setAppBadge).not.toHaveBeenCalledWith(3);
    await act(async () => listed(json(200, [vault("Work"), { ...vault("Home"), discreet: true }])));
    await waitFor(() => expect(queryClient.getQueryState(["vaults"])?.fetchStatus).toBe("idle"));
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(1));
    expect(setAppBadge).not.toHaveBeenCalledWith(3);
  });

  it("without the Badging API nothing breaks", async () => {
    shell(() => count(3, false));
    renderApp("/notes/Work/inbox.md");

    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("3 on"));
  });

  it("the count refreshes when the app comes back to the foreground", async () => {
    let n = 1;
    const fetch = shell(() => count(n, false));
    renderApp("/notes/Work/inbox.md");
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("1 on"));
    const before = asked(fetch);
    n = 2;
    await act(async () => void window.dispatchEvent(new Event("visibilitychange")));
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("2 on"));
    expect(asked(fetch)).toBe(before + 1);
  });

  it("the count refreshes after a change to an entry", async () => {
    let n = 2;
    const task = openTask("inbox", { path: "/Work/inbox.md" });
    shell(() => count(n, true), {
      "GET /api/vaults/Work/calendar/entry": () => json(200, noteEntry(n === 2 ? task : { ...task, task: "closed", state: "done" })),
      "POST /api/vaults/Work/calendar/close": () => {
        n = 1;
        return json(200, writtenView(task.path, { ...task, task: "closed", state: "done" }));
      },
    });
    renderApp("/notes/Work/inbox.md");
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("2 on, some overdue"));
    await userEvent.click(await screen.findByRole("button", { name: "Done: inbox" }));
    await waitFor(async () => expect(await treeButton()).toHaveAccessibleDescription("1 on, overdue"));
  });

  it("signing out clears the app icon badge", async () => {
    badging();
    shell(() => count(3, true), { "DELETE /api/session": () => noContent() });
    const router = renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(3));
    await userEvent.click(await treeButton());
    await userEvent.click(within(drawer()).getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(clearAppBadge).toHaveBeenCalled();
  });
});
