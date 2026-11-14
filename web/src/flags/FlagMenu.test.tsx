// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
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
});

const CERT = "/Work/Certificate.md";
const flagView = (flag: string | null, extra: Partial<FlagView> = {}): FlagView => ({ flag, inText: null, openTask: false, canWrite: true, ...extra });

/** Work's Certificate open, its flag as `view` says; a POST sets the flag it names (and says so in `view` from then on). */
function flagged(initial: FlagView, level = "owner", onPost?: () => Response | undefined) {
  let view = initial;
  const fetch = noteWithEntry(
    CERT,
    noteEntry(null, level !== "read"),
    {
      "GET /api/vaults/Work/flag": () => json(200, view),
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

async function openFromMenu() {
  await screen.findByRole("heading", { level: 1, name: "Note" });
  await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
  await userEvent.click(await screen.findByRole("menuitem", { name: "Flag" }));
  return flagSheet();
}

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
    await screen.findByRole("status", { name: "Flag changed" });
    expect(await posts(fetch)).toEqual([{ path: CERT, flag: "done" }]);
  });

  it("no question for a card or a note that isn't a task", async () => {
    const fetch = flagged(flagView(null, { openTask: false }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Done" }));
    await screen.findByRole("status", { name: "Flag changed" });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(await posts(fetch)).toEqual([{ path: CERT, flag: "done" }]);
  });

  it("Undo puts the old flag back, and reopens after Mark done", async () => {
    const fetch = flagged(flagView("red", { openTask: true }));
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await openFromMenu()).getByRole("button", { name: "Done" }));
    await userEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Mark done" }));
    const toast = await screen.findByRole("status", { name: "Flag changed" });
    expect(toast).toHaveTextContent("Flagged Done and marked done · Undo");
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

  it("a refusal is said in the menu, which stays open", async () => {
    flagged(flagView(null), "owner", () => json(400, { error: "I can't change this note's tags; edit them in the note" }));
    renderApp(`/notes${CERT}`);
    const sheet = await openFromMenu();
    await userEvent.click(within(sheet).getByRole("button", { name: "Red" }));
    expect(await within(sheet).findByText("I can't change this note's tags; edit them in the note")).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: "Flag changed" })).not.toBeInTheDocument();
  });
});
