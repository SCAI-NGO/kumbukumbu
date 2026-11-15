// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { json, renderApp, user } from "../test/app";
import { bodiesOf, entryView, noteEntry, noteWithEntry, openTask, writtenView } from "../test/calendar";
import type { EntryView } from "./api";
import { calendarItems } from "./DateDialog";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
});

const PLAN = "/Work/Plan.md";

async function openDialog(item: "Add to calendar…" | "Edit date…") {
  await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
  await userEvent.click(await screen.findByRole("menuitem", { name: item }));
  return screen.findByRole("dialog", { name: item.replace("…", "") });
}

describe("the date dialog", () => {
  it("the date dialog writes when, until, to do and show early", async () => {
    const fetch = noteWithEntry(PLAN, noteEntry(null), { "POST /api/vaults/Work/calendar/set": () => json200(PLAN) });
    renderApp(`/notes${PLAN}`);
    const dialog = await openDialog("Add to calendar…");
    const when = within(dialog).getByRole("group", { name: "When" });
    await userEvent.type(within(when).getByLabelText("Day"), "2026-11-20");
    await userEvent.type(within(when).getByLabelText("Time"), "14:00");
    const until = within(dialog).getByRole("group", { name: /^Until/ });
    await userEvent.type(within(until).getByLabelText("Time"), "15:00");
    expect(within(dialog).queryByText(/Show early/)).not.toBeInTheDocument();
    const todo = within(dialog).getByRole("checkbox", { name: "To do" });
    expect(todo).toHaveAccessibleDescription("something to get done, with a ✓");
    await userEvent.click(todo);
    const early = within(dialog).getByRole("spinbutton", { name: "Show early: days before" });
    // A few digits wide, with "days before" beside it on the same line.
    expect(early).toHaveClass("w-24");
    expect(early).not.toHaveClass("w-full");
    expect(early.parentElement).toHaveClass("flex-none");
    expect(early.parentElement).toHaveTextContent(/^days before$/);
    expect(within(dialog).getByRole("button", { name: "Day" })).toHaveClass("min-h-12");
    await userEvent.type(early, "7");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(async () =>
      expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([
        { path: PLAN, when: "2026-11-20T14:00", until: "2026-11-20T15:00", task: true, upcoming: "7d" },
      ]),
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("the dialog never asks about folders", async () => {
    noteWithEntry(PLAN, noteEntry(null));
    renderApp(`/notes${PLAN}`);
    const dialog = await openDialog("Add to calendar…");
    expect(within(dialog).queryByText(/folder/i)).not.toBeInTheDocument();
    expect(within(dialog).queryByLabelText(/folder/i)).not.toBeInTheDocument();
  });

  it("gives a whole week", async () => {
    const fetch = noteWithEntry(PLAN, noteEntry(null), { "POST /api/vaults/Work/calendar/set": () => json200(PLAN) });
    renderApp(`/notes${PLAN}`);
    const dialog = await openDialog("Add to calendar…");
    await userEvent.click(within(dialog).getByRole("button", { name: "Week" }));
    const week = within(within(dialog).getByRole("group", { name: "When" })).getByLabelText(/Week|A day in the week/);
    fireEvent.change(week, { target: { value: (week as HTMLInputElement).type === "week" ? "2026-W47" : "2026-11-18" } });
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: PLAN, when: "2026-W47" }]));
  });

  it("edits an entry's date from what it has, taking away an until left empty", async () => {
    const report = openTask("Plan", { when: "2026-12-15", until: "2026-12-18", upcoming: "1w", lastDay: "2026-12-18" });
    const fetch = noteWithEntry(PLAN, noteEntry(report), { "POST /api/vaults/Work/calendar/set": () => json200(PLAN) });
    renderApp(`/notes${PLAN}`);
    const dialog = await openDialog("Edit date…");
    const when = within(dialog).getByRole("group", { name: "When" });
    const until = within(dialog).getByRole("group", { name: /^Until/ });
    expect(within(when).getByLabelText("Day")).toHaveValue("2026-12-15");
    expect(within(until).getByLabelText("Day")).toHaveValue("2026-12-18");
    expect(within(dialog).getByRole("checkbox", { name: "To do" })).toBeChecked();
    expect(within(dialog).getByRole("spinbutton", { name: "Show early: days before" })).toHaveValue(7);

    await userEvent.clear(within(until).getByLabelText("Day"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: PLAN, when: "2026-12-15", clearUntil: true }]));
  });

  it("keeps the dialog open with the server's sentence when it refuses", async () => {
    noteWithEntry(PLAN, noteEntry(entryView("Plan")), {
      "POST /api/vaults/Work/calendar/set": () => new Response(JSON.stringify({ error: "--when 2026-02-30 is not a date" }), { status: 400, headers: { "Content-Type": "application/json" } }),
    });
    renderApp(`/notes${PLAN}`);
    const dialog = await openDialog("Edit date…");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(await within(dialog).findByText("--when 2026-02-30 is not a date")).toBeInTheDocument();
  });

  it("a date refused for a note of a discreet vault is said without the vault", async () => {
    const home = "/Home/Plan.md";
    const vault = (name: string, discreet: boolean) => ({ name, notes: 1, embeddings: "internal", canWrite: true, level: "owner", discreet });
    noteWithEntry(home, noteEntry(null), {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: true, until: null } }),
      "GET /api/vaults": () => json(200, [vault("Work", false), vault("Home", true)]),
      "GET /api/vaults/Home/tree": () => json(200, { damaged: 0, root: { name: "Home", path: "/Home", folders: [], notes: [{ name: "Plan.md", path: home, size: 10 }] } }),
      "GET /api/vaults/Home/calendar/entry": () => json(200, noteEntry(null)),
      "POST /api/vaults/Home/calendar/set": () => json(404, { error: "There is no vault 'Home'." }),
    });
    renderApp(`/notes${home}`);
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Home"]));
    const dialog = await openDialog("Add to calendar…");
    await userEvent.type(within(within(dialog).getByRole("group", { name: "When" })).getByLabelText("Day"), "2026-11-20");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(await within(dialog).findByText("There is no such note (if it was removed, it is in the trash).")).toBeInTheDocument();
    expect(dialog).not.toHaveTextContent("Home");
  });

  it("a refused date in an ordinary vault keeps the server's sentence", async () => {
    noteWithEntry(PLAN, noteEntry(null), { "POST /api/vaults/Work/calendar/set": () => json(404, { error: "There is no note /Work/Plan.md." }) });
    renderApp(`/notes${PLAN}`);
    const dialog = await openDialog("Add to calendar…");
    await userEvent.type(within(within(dialog).getByRole("group", { name: "When" })).getByLabelText("Day"), "2026-11-20");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(await within(dialog).findByText("There is no note /Work/Plan.md.")).toBeInTheDocument();
  });
});

describe("a to-do, or just an appointment", () => {
  const labels = (entry: EntryView | null, canWrite = true) =>
    calendarItems(PLAN, { data: noteEntry(entry, canWrite), isError: false }, () => {}, () => {}, () => {}).map((i) => i.label);
  const closed = openTask("Plan", { task: "closed", state: "done", closed: "2026-11-13" });

  it.each<[string, EntryView | null, boolean, string[]]>([
    ["a note given a day", entryView("Plan"), true, ["Make it a to-do"]],
    ["an open to-do", openTask("Plan"), true, ["Just an appointment"]],
    ["a closed to-do, which would lose its closed and its reason", closed, true, []],
    ["a day note, an entry by its name", entryView("Plan", { byName: true }), true, []],
    ["a week note that is a to-do", openTask("Plan", { byName: true }), true, []],
    ["a board card", entryView("Plan", { kind: "card", column: "Doing" }), true, []],
    ["a note without a date", null, true, []],
    ["a note one may not write", entryView("Plan"), false, []],
  ])("%s", (_, entry, canWrite, offered) => {
    expect(labels(entry, canWrite).filter((l) => l === "Make it a to-do" || l === "Just an appointment")).toEqual(offered);
  });

  it("offers neither where the menu gives no way to switch", () => {
    const items = calendarItems(PLAN, { data: noteEntry(entryView("Plan")), isError: false }, () => {}, () => {}).map((i) => i.label);
    expect(items).toEqual(["Edit date…", "Remove from calendar"]);
  });

  it("Make it a to-do sets only task, keeping the rest of the date as it is", async () => {
    const dentist = entryView("Plan", { when: "2026-11-19T14:00", until: "2026-11-19T15:00", time: "14:00" });
    const fetch = noteWithEntry(PLAN, noteEntry(dentist), { "POST /api/vaults/Work/calendar/set": () => json200(PLAN) });
    renderApp(`/notes${PLAN}`);
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Make it a to-do" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: PLAN, task: true }]));
    expect(await screen.findByText("It's a to-do now.")).toBeInTheDocument();
  });

  it("Just an appointment takes the to-do away, and says so", async () => {
    const fetch = noteWithEntry(PLAN, noteEntry(openTask("Plan")), { "POST /api/vaults/Work/calendar/set": () => json200(PLAN) });
    renderApp(`/notes${PLAN}`);
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Just an appointment" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: PLAN, task: false }]));
    expect(await screen.findByText("Just an appointment now.")).toBeInTheDocument();
  });

  it("says the server's sentence when it refuses", async () => {
    noteWithEntry(PLAN, noteEntry(entryView("Plan")), { "POST /api/vaults/Work/calendar/set": () => json(409, { error: "The note changed meanwhile." }) });
    renderApp(`/notes${PLAN}`);
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Make it a to-do" }));
    expect(await screen.findByText("The note changed meanwhile.")).toBeInTheDocument();
  });
});

function json200(path: string) {
  return new Response(JSON.stringify(writtenView(path)), { status: 200, headers: { "Content-Type": "application/json" } });
}
