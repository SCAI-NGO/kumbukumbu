// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { json, renderApp } from "../test/app";
import { bodiesOf, entryView, noteEntry, noteWithEntry, openTask, writtenView } from "../test/calendar";
import type { NoteEntryView } from "./api";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const CERT = "/Work/Certificate.md";
const line = () => screen.findByRole("group", { name: "In the calendar" });
const closedTask = (reason: string | null) => openTask("Certificate", { task: "closed", closed: "2026-12-16", reason, state: "done", span: null });

describe("the note's header line", () => {
  it("shows the header line of an open task", async () => {
    noteWithEntry(CERT, noteEntry(openTask()));
    renderApp(`/notes${CERT}`);
    const header = await line();
    expect(header).toHaveTextContent(/^📅 Tue 15 Dec · To do · ✓ · Later$/);
    expect(within(header).getByRole("link", { name: "Tue 15 Dec" })).toHaveAttribute("href", "/calendar/Work?week=2026-W51");
    expect(within(header).getByRole("button", { name: "Done: Certificate" })).toBeInTheDocument();
    expect(within(header).getByRole("button", { name: "Later: Certificate" })).toBeInTheDocument();
  });

  it("shows a note given a day with its time, and nothing to tick", async () => {
    noteWithEntry("/Work/Zahnarzt.md", noteEntry(entryView("Zahnarzt", { when: "2026-11-20T14:00", time: "14:00", firstDay: "2026-11-20", lastDay: "2026-11-20" })));
    renderApp("/notes/Work/Zahnarzt.md");
    const header = await line();
    expect(header).toHaveTextContent(/^📅 Fri 20 Nov · 14:00$/);
    expect(within(header).queryByRole("button")).not.toBeInTheDocument();
  });

  it("says nothing for a note without a time", async () => {
    const fetch = noteWithEntry(CERT, noteEntry(null));
    renderApp(`/notes${CERT}`);
    await screen.findByRole("heading", { level: 1, name: "Note" });
    await waitFor(() => expect(fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname === "/api/vaults/Work/calendar/entry")).toBe(true));
    expect(screen.queryByRole("group", { name: "In the calendar" })).not.toBeInTheDocument();
  });

  it("the tick closes at once and offers Undo and a reason", async () => {
    let entry: NoteEntryView = noteEntry(openTask());
    const fetch = noteWithEntry(CERT, () => entry, {
      "POST /api/vaults/Work/calendar/close": () => {
        entry = noteEntry(closedTask(null));
        return json(200, writtenView(CERT, entry.entry));
      },
      "POST /api/vaults/Work/calendar/reopen": () => {
        entry = noteEntry(openTask());
        return json(200, writtenView(CERT, entry.entry, "v3"));
      },
    });
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await line()).getByRole("button", { name: "Done: Certificate" }));
    // No dialog: closing is the most frequent action.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const toast = await screen.findByRole("status", { name: "Closed" });
    expect(toast).toHaveTextContent("Closed · Undo · Add a reason");
    expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/close")).toEqual([{ path: CERT }]);
    await waitFor(async () => expect(await line()).toHaveTextContent(/^📅 Tue 15 Dec · Closed 16 Dec · Reopen · Add a reason$/));

    await userEvent.click(within(toast).getByRole("button", { name: "Undo" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/reopen")).toEqual([{ path: CERT }]));
    await waitFor(() => expect(screen.queryByRole("status", { name: "Closed" })).not.toBeInTheDocument());
    await waitFor(async () => expect(await line()).toHaveTextContent(/^📅 Tue 15 Dec · To do · ✓ · Later$/));
  });

  it("the toast's Add a reason opens a one-line field that Enter saves", async () => {
    const fetch = noteWithEntry(CERT, noteEntry(openTask()), {
      "POST /api/vaults/Work/calendar/close": () => json(200, writtenView(CERT, closedTask(null))),
      "POST /api/vaults/Work/calendar/set": () => json(200, writtenView(CERT, closedTask("not needed any more"), "v3")),
    });
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await line()).getByRole("button", { name: "Done: Certificate" }));
    const toast = await screen.findByRole("status", { name: "Closed" });
    await userEvent.click(within(toast).getByRole("button", { name: "Add a reason" }));
    const field = within(toast).getByRole("textbox", { name: "Reason" });
    expect(field).toHaveFocus();
    await userEvent.type(field, "not needed any more{Enter}");
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: CERT, reason: "not needed any more" }]));
    await waitFor(() => expect(screen.queryByRole("status", { name: "Closed" })).not.toBeInTheDocument());
  });

  it("adds a reason a week later from the closed line", async () => {
    const fetch = noteWithEntry(CERT, noteEntry(closedTask(null)), {
      "POST /api/vaults/Work/calendar/set": () => json(200, writtenView(CERT, closedTask("renewed until 2027"))),
    });
    renderApp(`/notes${CERT}`);
    const header = await line();
    expect(header).toHaveTextContent(/^📅 Tue 15 Dec · Closed 16 Dec · Reopen · Add a reason$/);
    await userEvent.click(within(header).getByRole("button", { name: "Add a reason" }));
    await userEvent.type(within(header).getByRole("textbox", { name: "Reason" }), "renewed until 2027{Enter}");
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: CERT, reason: "renewed until 2027" }]));
  });

  it("a closed task's line says when and why, with Edit reason", async () => {
    noteWithEntry(CERT, noteEntry(closedTask("renewed until 2027")));
    renderApp(`/notes${CERT}`);
    const header = await line();
    expect(header).toHaveTextContent(/^📅 Tue 15 Dec · Closed 16 Dec · renewed until 2027 · Reopen · Edit reason$/);
    await userEvent.click(within(header).getByRole("button", { name: "Edit reason" }));
    expect(within(header).getByRole("textbox", { name: "Reason" })).toHaveValue("renewed until 2027");
  });

  it("a day note by name says so and has no Remove", async () => {
    const day = "/Work/Diary/2026-11-12.md";
    noteWithEntry(day, noteEntry(entryView("2026-11-12", { path: day, byName: true })));
    renderApp(`/notes${day}`);
    expect(await line()).toHaveTextContent(/^📅 Thursday, 12 November · This is the day note of 12 November \(by its name\)$/);
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    expect(screen.getByRole("menuitem", { name: "Edit date…" })).toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Remove from calendar" })).not.toBeInTheDocument();
  });

  it("a card shows its column and Move to Done", async () => {
    const card = "/Work/Board/Release.md";
    const release = entryView("Release", { path: card, kind: "card", task: "open", when: "2026-11-20", firstDay: "2026-11-20", lastDay: "2026-11-20", column: "In progress", boardAction: "Done", state: "upcoming" });
    const fetch = noteWithEntry(card, noteEntry(release), {
      "POST /api/vaults/Work/board/move": () => json(200, { columns: [] }),
    });
    renderApp(`/notes${card}`);
    const header = await line();
    expect(header).toHaveTextContent(/^📅 Fri 20 Nov · In progress · Move to Done · Later$/);
    await userEvent.click(within(header).getByRole("button", { name: "Move to Done" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/board/move")).toEqual([{ card: "Release", column: "Done" }]));
  });

  it("a card on a board without one closing column leads to the board", async () => {
    const card = "/Work/Board/Release.md";
    noteWithEntry(card, noteEntry(entryView("Release", { path: card, kind: "card", column: "In progress", state: "upcoming", firstDay: "2026-11-20", lastDay: "2026-11-20" })));
    renderApp(`/notes${card}`);
    const header = await line();
    expect(within(header).queryByRole("button", { name: /^Move to/ })).not.toBeInTheDocument();
    expect(within(header).getByRole("link", { name: "Open the board" })).toHaveAttribute("href", "/board/Work");
  });

  it("a read-only member sees the line without tick, Later, Reopen or reason, and no calendar items", async () => {
    noteWithEntry(CERT, noteEntry(openTask(), false), {}, "read");
    renderApp(`/notes${CERT}`);
    const header = await line();
    expect(header).toHaveTextContent(/^📅 Tue 15 Dec · To do$/);
    expect(within(header).queryByRole("button")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    expect(screen.queryByRole("menuitem", { name: /calendar|Edit date/ })).not.toBeInTheDocument();
    cleanup();

    noteWithEntry(CERT, noteEntry(closedTask(null), false), {}, "read");
    renderApp(`/notes${CERT}`);
    expect(await line()).toHaveTextContent(/^📅 Tue 15 Dec · Closed 16 Dec$/);
    expect(within(await line()).queryByRole("button")).not.toBeInTheDocument();
  });

  it("the tick waits for the editor's save and sends nothing when it fails", async () => {
    const fetch = noteWithEntry(CERT, noteEntry(openTask()), { "POST /api/vaults/Work/calendar/close": () => json(200, writtenView(CERT, closedTask(null))) });
    renderApp(`/notes${CERT}`);
    const header = await line();
    const save = vi.fn(async () => false);
    useEditor.setState({ beforeRead: save });
    await userEvent.click(within(header).getByRole("button", { name: "Done: Certificate" }));
    expect(await within(header).findByText("Your typing isn't saved yet; try again in a moment.")).toBeInTheDocument();
    expect(save).toHaveBeenCalled();
    expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/close")).toEqual([]);
    expect(screen.queryByRole("status", { name: "Closed" })).not.toBeInTheDocument();
  });

  it("in Write mode the tick ends writing first, and typing after it gives no conflict", async () => {
    // The server's note: the close writes a version of its own on top of the one the editor saved.
    const server = { content: "# Note\n", version: "v1", n: 1 };
    let entry = noteEntry(openTask());
    const order: string[] = [];
    const puts: { content: string; version: string }[] = [];
    const now = () => ({ path: CERT, content: server.content, version: server.version, lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" });
    const fetch = noteWithEntry(CERT, () => entry, {
      [`GET /api/notes${CERT}`]: () => json(200, now()),
      [`GET /api/version${CERT}`]: () => json(200, { path: CERT, version: server.version, lastWriter: "ada", lastWriteAt: "2026-11-13T09:00:00Z" }),
      [`PUT /api/notes${CERT}`]: async (r) => {
        const body = (await r.json()) as { content: string; version: string };
        puts.push(body);
        order.push("save");
        if (body.version !== server.version) return json(409, { error: "changed", current: now() });
        server.content = body.content;
        server.version = `v${++server.n}`;
        return json(200, now());
      },
      "POST /api/vaults/Work/calendar/close": () => {
        order.push("close");
        server.content = `---\ntask: closed\n---\n${server.content}`;
        server.version = `v${++server.n}`;
        entry = noteEntry(closedTask(null));
        return json(200, writtenView(CERT, entry.entry, server.version));
      },
    });
    renderApp(`/notes${CERT}`);
    await line();
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    const editor = await screen.findByRole("textbox", { name: "Markdown source" });
    await userEvent.type(editor, "Ordered.");
    await userEvent.click(within(await line()).getByRole("button", { name: "Done: Certificate" }));

    await screen.findByRole("status", { name: "Closed" });
    expect(order).toEqual(["save", "close"]);
    expect(puts[0]).toEqual({ content: "# Note\nOrdered.", version: "v1" });
    expect(useEditor.getState().mode).toBe("read");
    expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/close")).toEqual([{ path: CERT }]);

    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    const again = await screen.findByRole("textbox", { name: "Markdown source" });
    expect(again).toHaveValue("---\ntask: closed\n---\n# Note\nOrdered.");
    await userEvent.type(again, " More.");
    await waitFor(() => expect(puts).toHaveLength(2), { timeout: 3000 });
    expect(puts[1].version).toBe("v3");
    expect(screen.queryByRole("alert", { name: "Changed elsewhere" })).not.toBeInTheDocument();
  });

  it("the message's reason field opens in the message, with a label and Save, also from the header's Add a reason", async () => {
    let entry: NoteEntryView = noteEntry(openTask());
    const fetch = noteWithEntry(CERT, () => entry, {
      "POST /api/vaults/Work/calendar/close": () => {
        entry = noteEntry(closedTask(null));
        return json(200, writtenView(CERT, entry.entry));
      },
      "POST /api/vaults/Work/calendar/set": () => {
        entry = noteEntry(closedTask("not needed any more"));
        return json(200, writtenView(CERT, entry.entry, "v3"));
      },
    });
    renderApp(`/notes${CERT}`);
    await userEvent.click(within(await line()).getByRole("button", { name: "Done: Certificate" }));
    const toast = await screen.findByRole("status", { name: "Closed" });
    // "Closed · Undo": the dot keeps its spaces in the flex row.
    for (const dot of within(toast).getAllByText("·", { selector: "span", normalizer: (t) => t.trim() })) expect(dot).toHaveClass("whitespace-pre");
    await waitFor(async () => expect(await line()).toHaveTextContent(/Closed 16 Dec · Reopen · Add a reason$/));
    await userEvent.click(within(await line()).getByRole("button", { name: "Add a reason" }));
    // One field, in the message, not at the top of the note.
    expect(within(await line()).queryByRole("textbox")).not.toBeInTheDocument();
    const field = within(toast).getByRole("textbox", { name: "Reason" });
    expect(field).toHaveAttribute("placeholder", "Reason");
    expect(field).toHaveFocus();
    await userEvent.type(field, "not needed any more");
    await userEvent.click(within(toast).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: CERT, reason: "not needed any more" }]));
    await waitFor(() => expect(screen.queryByRole("status", { name: "Closed" })).not.toBeInTheDocument());
    await waitFor(async () => expect(await line()).toHaveTextContent(/^📅 Tue 15 Dec · Closed 16 Dec · not needed any more · Reopen · Edit reason$/));
  });

  it("the header's field has Save too, and its controls are 48 px tall", async () => {
    const fetch = noteWithEntry(CERT, noteEntry(closedTask(null)), {
      "POST /api/vaults/Work/calendar/set": () => json(200, writtenView(CERT, closedTask("renewed"))),
    });
    renderApp(`/notes${CERT}`);
    const header = await line();
    expect(within(header).getByRole("link", { name: "Tue 15 Dec" })).toHaveClass("min-h-12");
    expect(within(header).getByRole("button", { name: "Reopen" })).toHaveClass("min-h-12");
    expect(within(header).getByRole("button", { name: "Add a reason" })).toHaveClass("min-h-12");
    await userEvent.click(within(header).getByRole("button", { name: "Add a reason" }));
    expect(within(header).getByRole("textbox", { name: "Reason" })).toHaveAttribute("placeholder", "Reason");
    await userEvent.type(within(header).getByRole("textbox", { name: "Reason" }), "renewed");
    await userEvent.click(within(header).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/set")).toEqual([{ path: CERT, reason: "renewed" }]));
  });

  it("an open to-do's tick and Later in the header are 48 px tall, and Later says where it moved", async () => {
    const moved = openTask("Certificate", { when: "2026-12-16", firstDay: "2026-12-16", lastDay: "2026-12-16" });
    noteWithEntry(CERT, noteEntry(openTask()), { "POST /api/vaults/Work/calendar/later": () => json(200, writtenView(CERT, moved)) });
    renderApp(`/notes${CERT}`);
    const header = await line();
    expect(within(header).getByRole("button", { name: "Done: Certificate" })).toHaveClass("min-h-12");
    expect(within(header).getByRole("button", { name: "Later: Certificate" })).toHaveClass("min-h-12");
    await userEvent.click(within(header).getByRole("button", { name: "Later: Certificate" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "+1 day" }));
    expect(await screen.findByRole("status", { name: "Moved" })).toHaveTextContent("Moved to Wed 16 Dec · Undo");
  });

  it("a card whose date couldn't be read leads to its board instead of Fix…", async () => {
    const card = "/Work/Board/Pump.md";
    noteWithEntry(card, { entry: null, couldntRead: { path: card, title: "Pump", plain: "This card's date can't be shown until its board can be read again", readable: {} }, canWrite: true });
    renderApp(`/notes${card}`);
    const header = await line();
    expect(within(header).queryByRole("button", { name: "Fix…" })).not.toBeInTheDocument();
    expect(within(header).getByRole("link", { name: "Open the board" })).toHaveAttribute("href", "/board/Work");
  });
});
