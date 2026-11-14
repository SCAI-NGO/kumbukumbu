// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { json, renderApp } from "../test/app";
import { bodiesOf, noteEntry, noteWithEntry } from "../test/calendar";
import type { FlagView } from "./api";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
});

const CERT = "/Work/Certificate.md";
const flagView = (flag: string | null, extra: Partial<FlagView> = {}): FlagView => ({ flag, inText: null, openTask: false, canWrite: true, ...extra });

/** Work's Certificate open, its flag as `view` says; a POST sets the flag it names (and says so in `view` from then on). */
function flagged(initial: FlagView, level = "owner", onPost?: () => Response | undefined, onGet?: () => Promise<FlagView | undefined>) {
  let view = initial;
  const fetch = noteWithEntry(
    CERT,
    noteEntry(null, level !== "read"),
    {
      "GET /api/vaults/Work/flag": async () => json(200, (await onGet?.()) ?? view),
      "POST /api/vaults/Work/flag": async (r) => {
        const answer = onPost?.();
        if (answer) return answer;
        const body = (await r.clone().json()) as { flag: string | null; calendar?: string };
        view = { ...view, flag: body.flag, openTask: body.calendar === "close" ? false : body.calendar === "reopen" ? true : view.openTask };
        return json(200, { path: CERT, version: "v2", note: view });
      },
    },
    level,
  );
  return fetch;
}

const header = () => screen.findByRole("group", { name: "Flag" });
const flagSheet = () => screen.findByRole("dialog", { name: "Flag" });
const posts = (fetch: ReturnType<typeof flagged>) => bodiesOf(fetch, "POST", "/api/vaults/Work/flag");

/** ⋯ → Flag, once the flag is read again (the choices wait for it). */
async function openFromMenu() {
  await screen.findByRole("heading", { level: 1, name: "Note" });
  await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
  await userEvent.click(await screen.findByRole("menuitem", { name: "Flag" }));
  const sheet = await flagSheet();
  await waitFor(() => expect(within(sheet).getByRole("button", { name: "No flag" })).toBeEnabled());
  return sheet;
}

const toastText = async () => (await screen.findByRole("status", { name: "Flag changed" })).textContent;

describe("the flag menu", () => {
  it("⋯ → Flag offers the eight and No flag, by icon and name", async () => {
    flagged(flagView("blue"));
    renderApp(`/notes${CERT}`);
    const sheet = await openFromMenu();
    const choices = within(sheet).getAllByRole("button").filter((b) => b.hasAttribute("aria-pressed"));
    expect(choices.map((b) => b.textContent)).toEqual(["Red", "Green", "Blue", "Purple", "Yellow", "White", "Done", "Important", "No flag"]);
    for (const choice of choices) expect(choice.querySelector("svg")).not.toBeNull();
    expect(within(sheet).getByRole("button", { name: "Blue" })).toHaveAttribute("aria-pressed", "true");
    expect(within(sheet).getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "false");
  });

  it("choosing Red posts flag red after the editor settled, and the note and the tree are read again", async () => {
    const order: string[] = [];
    const fetch = flagged(flagView(null), "owner", () => void order.push("post"));
    renderApp(`/notes${CERT}`);
    const sheet = await openFromMenu();
    const gets = (path: string) => fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname === path).length;
    const [notes, trees] = [gets(`/api/notes${CERT}`), gets("/api/vaults/Work/tree")];
    useEditor.setState({
      beforeRead: vi.fn(async () => {
        order.push("settled");
        return true;
      }),
    });
    await userEvent.click(within(sheet).getByRole("button", { name: "Red" }));
    const toast = await screen.findByRole("status", { name: "Flag changed" });
    expect(toast).toHaveTextContent("Flagged Red · Undo");
    expect(order).toEqual(["settled", "post"]);
    expect(screen.queryByRole("dialog", { name: "Flag" })).not.toBeInTheDocument();
    await waitFor(() => expect(gets(`/api/notes${CERT}`)).toBeGreaterThan(notes));
    await waitFor(() => expect(gets("/api/vaults/Work/tree")).toBeGreaterThan(trees));
  });

  it("sends nothing when the editor's save fails", async () => {
    const fetch = flagged(flagView(null));
    renderApp(`/notes${CERT}`);
    const sheet = await openFromMenu();
    useEditor.setState({ beforeRead: vi.fn(async () => false) });
    await userEvent.click(within(sheet).getByRole("button", { name: "Red" }));
    expect(await within(sheet).findByText("Your typing isn't saved yet; try again in a moment.")).toBeInTheDocument();
    expect(await posts(fetch)).toEqual([]);
  });

  it("a read-only member sees the flag and no menu", async () => {
    flagged(flagView("red", { canWrite: false }), "read");
    renderApp(`/notes${CERT}`);
    const line = await header();
    expect(within(line).getByRole("img", { name: "Red flag" })).toBeInTheDocument();
    expect(within(line).queryByRole("button")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    expect(screen.queryByRole("menuitem", { name: "Flag" })).not.toBeInTheDocument();
  });

  it("a text flag says where to change it and offers nothing", async () => {
    const fetch = flagged(flagView("red", { inText: "#flag/red" }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await header()).getByRole("button", { name: "Red flag" }));
    const sheet = await flagSheet();
    expect(sheet).toHaveTextContent("This flag is written in the note's text (#flag/red); change it there.");
    expect(within(sheet).queryByRole("button", { name: "Red" })).not.toBeInTheDocument();
    expect(within(sheet).queryByRole("button", { name: "No flag" })).not.toBeInTheDocument();
    expect(await posts(fetch)).toEqual([]);
  });

  it("✓ on an open task asks Also mark it done in the calendar?; Mark done closes it in the same write", async () => {
    const fetch = flagged(flagView("red", { openTask: true }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Done" }));
    const question = await screen.findByRole("alertdialog", { name: "Also mark it done in the calendar?" });
    expect(await posts(fetch)).toEqual([]);
    await userEvent.click(within(question).getByRole("button", { name: "Mark done" }));
    await screen.findByRole("status", { name: "Flag changed" });
    expect(await posts(fetch)).toEqual([{ path: CERT, flag: "done", calendar: "close" }]);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("✓ on an open task with Just the flag sets the tick alone", async () => {
    const fetch = flagged(flagView(null, { openTask: true }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Done" }));
    await userEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Just the flag" }));
    expect(await toastText()).toBe("Ticked · Undo");
    expect(await posts(fetch)).toEqual([{ path: CERT, flag: "done" }]);
  });

  it("no question for a card or a note that isn't a task", async () => {
    const fetch = flagged(flagView(null, { openTask: false }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Done" }));
    expect(await toastText()).toBe("Ticked · Undo");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(await posts(fetch)).toEqual([{ path: CERT, flag: "done" }]);
  });

  it("Undo puts the old flag back, and reopens after Mark done", async () => {
    const fetch = flagged(flagView("red", { openTask: true }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Done" }));
    await userEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Mark done" }));
    const toast = await screen.findByRole("status", { name: "Flag changed" });
    expect(toast).toHaveTextContent("Ticked and marked done · Undo");
    await userEvent.click(within(toast).getByRole("button", { name: "Undo" }));
    await waitFor(async () => expect((await posts(fetch)).at(-1)).toEqual({ path: CERT, flag: "red", calendar: "reopen" }));
    await waitFor(() => expect(screen.queryByRole("status", { name: "Flag changed" })).not.toBeInTheDocument());
  });

  it("No flag says Flag removed, and Undo puts the flag back", async () => {
    const fetch = flagged(flagView("purple"));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "No flag" }));
    const toast = await screen.findByRole("status", { name: "Flag changed" });
    expect(toast).toHaveTextContent("Flag removed · Undo");
    await userEvent.click(within(toast).getByRole("button", { name: "Undo" }));
    await waitFor(async () => expect(await posts(fetch)).toEqual([{ path: CERT, flag: null }, { path: CERT, flag: "purple" }]));
  });

  it.each([
    ["Important", "Marked important · Undo"],
    ["Red", "Flagged Red · Undo"],
    ["White", "Flagged White · Undo"],
  ])("choosing %s says %s", async (choice, text) => {
    flagged(flagView(null));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: choice }));
    expect(await toastText()).toBe(text);
  });

  it("after a write every view that shows tags, history, changes or the calendar is asked again, and the flag is the write's answer", async () => {
    flagged(flagView(null));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, "invalidateQueries");
    renderApp(`/notes${CERT}`, queryClient);
    const sheet = await openFromMenu();
    invalidated.mockClear();
    await userEvent.click(within(sheet).getByRole("button", { name: "Red" }));
    await screen.findByRole("status", { name: "Flag changed" });
    const keys = invalidated.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
    for (const key of [["tags"], ["history", CERT], ["changes"], ["calendar"], ["whats-on"], ["note", CERT], ["tree", "Work"], ["search"], ["recent"]])
      expect(keys).toContain(JSON.stringify(key));
    expect(keys).not.toContain(JSON.stringify(["flag", CERT]));
    expect(queryClient.getQueryData(["flag", CERT])).toMatchObject({ flag: "red" });
  });

  it("the choices wait while the flag is read again, so Undo puts back the flag the note has now", async () => {
    let release = () => {};
    let gated = false;
    // Changed elsewhere since the header read it: blue then, red now, and the read of that is slow.
    const fetch = flagged(flagView("blue"), "owner", undefined, async () => {
      if (!gated) return undefined;
      await new Promise<void>((go) => (release = go));
      return flagView("red");
    });
    renderApp(`/notes${CERT}`);
    await within(await header()).findByRole("button", { name: "Blue flag" });
    gated = true;
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Flag" }));
    const sheet = await flagSheet();
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "No flag" })).toBeDisabled());
    gated = false;
    release();
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "true"));
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "No flag" })).toBeEnabled());
    await userEvent.click(within(sheet).getByRole("button", { name: "No flag" }));
    await userEvent.click(within(await screen.findByRole("status", { name: "Flag changed" })).getByRole("button", { name: "Undo" }));
    await waitFor(async () => expect((await posts(fetch)).at(-1)).toEqual({ path: CERT, flag: "red" }));
  });

  it("No flag from the header's button gives the focus to the note's ⋯, as the button is gone", async () => {
    flagged(flagView("green"));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await header()).getByRole("button", { name: "Green flag" }));
    const sheet = await flagSheet();
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "No flag" })).toBeEnabled());
    await userEvent.click(within(sheet).getByRole("button", { name: "No flag" }));
    await screen.findByRole("status", { name: "Flag changed" });
    await waitFor(() => expect(screen.queryByRole("group", { name: "Flag" })).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole("button", { name: "Note actions" })).toHaveFocus());
  });

  it("the menu and its message go when the note's vault is hidden again", async () => {
    flagged(flagView(null));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Red" }));
    await screen.findByRole("status", { name: "Flag changed" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Flag" }));
    await flagSheet();
    act(() => useDiscreet.setState({ gone: new Set(["Work"]) }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Flag" })).not.toBeInTheDocument());
    expect(screen.queryByRole("status", { name: "Flag changed" })).not.toBeInTheDocument();
  });

  it("one message for every place that offers the menu: the newest replaces the old, and its Undo takes back the latest write", async () => {
    const fetch = flagged(flagView(null));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Red" }));
    expect(await toastText()).toBe("Flagged Red · Undo");
    // Within the message's time, from the header's flag this time.
    await userEvent.click(within(await header()).getByRole("button", { name: "Red flag" }));
    const sheet = await flagSheet();
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "Blue" })).toBeEnabled());
    await userEvent.click(within(sheet).getByRole("button", { name: "Blue" }));
    await waitFor(() => expect(screen.getAllByRole("status", { name: "Flag changed" })).toHaveLength(1));
    const toast = screen.getByRole("status", { name: "Flag changed" });
    expect(toast).toHaveTextContent("Flagged Blue · Undo");
    await userEvent.click(within(toast).getByRole("button", { name: "Undo" }));
    await waitFor(async () => expect((await posts(fetch)).at(-1)).toEqual({ path: CERT, flag: "red" }));
    await waitFor(() => expect(screen.queryByRole("status", { name: "Flag changed" })).not.toBeInTheDocument());
  });

  it("the message keeps out of the way while Also mark it done in the calendar? is asked", async () => {
    flagged(flagView(null, { openTask: true }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Red" }));
    expect(await toastText()).toBe("Flagged Red · Undo");
    await userEvent.click(within(await header()).getByRole("button", { name: "Red flag" }));
    const sheet = await flagSheet();
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "Done" })).toBeEnabled());
    await userEvent.click(within(sheet).getByRole("button", { name: "Done" }));
    const question = await screen.findByRole("alertdialog", { name: "Also mark it done in the calendar?" });
    expect(screen.queryByRole("status", { name: "Flag changed" })).not.toBeInTheDocument();
    await userEvent.click(within(question).getByRole("button", { name: "Mark done" }));
    expect(await toastText()).toBe("Ticked and marked done · Undo");
  });

  it("a refusal is said in the menu, which stays open", async () => {
    flagged(flagView(null), "owner", () => json(400, { error: "I can't change this note's tags; edit them in the note" }));
    renderApp(`/notes${CERT}`);
    const sheet = await openFromMenu();
    await userEvent.click(within(sheet).getByRole("button", { name: "Red" }));
    expect(await within(sheet).findByText("I can't change this note's tags; edit them in the note")).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: "Flag changed" })).not.toBeInTheDocument();
  });
});
