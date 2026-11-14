// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { renderApp } from "../test/app";
import { bodiesOf, entryView, noteEntry, noteWithEntry, openTask, writtenView } from "../test/calendar";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
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
});

function json200(path: string) {
  return new Response(JSON.stringify(writtenView(path)), { status: 200, headers: { "Content-Type": "application/json" } });
}
