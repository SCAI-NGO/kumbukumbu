// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { json, renderApp, user } from "../test/app";
import { entryView, noteEntry, noteWithEntry } from "../test/calendar";
import { Properties } from "./NoteView";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  useSession.getState().reset();
  useEditor.getState().reset();
  localStorage.removeItem("kumbu.propertiesOpen");
});

const ZAHNARZT = "/Work/Zahnarzt.md";
const PUMP = "/Work/Board/Pump.md";
const dated = noteEntry(entryView("Zahnarzt", { when: "2026-11-20", firstDay: "2026-11-20", lastDay: "2026-11-20" }));
const calendarNote = ["---", "when: 2026-11-20", "task: open", "upcoming: 2d", "tags: [work]", "---", "# Zahnarzt", ""].join("\n");
const flagged = ["---", "tags: [flag/important, work]", "owner: Alice", "---", "# Zahnarzt", ""].join("\n");
const card = (moved: string | null) => ["---", "column: Running", "rank: a0", ...(moved ? [`moved: ${moved}`] : []), "---", "Fix the pump.", ""].join("\n");

const box = () => document.querySelector("dl[aria-label=Properties]");
const toggle = () => screen.findByRole("button", { name: /^Properties ·/ });

describe("the properties box", () => {
  it("a calendar note's properties fold to Properties · N, and a tap opens them", async () => {
    noteWithEntry(ZAHNARZT, dated, {}, "owner", calendarNote);
    renderApp(`/notes${ZAHNARZT}`);
    const button = await screen.findByRole("button", { name: "Properties · 4" });
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(document.querySelector("dl")).toBeNull();

    await userEvent.click(button);
    expect(button).toHaveAttribute("aria-expanded", "true");
    expect(box()).toHaveTextContent("when");

    await userEvent.click(button);
    expect(document.querySelector("dl")).toBeNull();
  });

  it("a flagged note folds whatever its other keys", async () => {
    noteWithEntry(ZAHNARZT, noteEntry(null), {}, "owner", flagged);
    renderApp(`/notes${ZAHNARZT}`);
    expect(await screen.findByRole("button", { name: "Properties · 2" })).toHaveAttribute("aria-expanded", "false");
    expect(document.querySelector("dl")).toBeNull();
  });

  it("a flag counts ignoring case, and only one of the flags", async () => {
    noteWithEntry(ZAHNARZT, noteEntry(null), {}, "owner", ["---", "tags: [Flag/Red]", "---", "# Zahnarzt", ""].join("\n"));
    renderApp(`/notes${ZAHNARZT}`);
    expect(await screen.findByRole("button", { name: "Properties · 1" })).toHaveAttribute("aria-expanded", "false");
    cleanup();

    noteWithEntry(ZAHNARZT, noteEntry(null), {}, "owner", ["---", "tags: [flag/maybe]", "---", "# Zahnarzt", ""].join("\n"));
    renderApp(`/notes${ZAHNARZT}`);
    await screen.findByRole("heading", { name: "Zahnarzt" });
    expect(box()).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Properties ·/ })).not.toBeInTheDocument();
  });

  it("a note in a sub-folder of Board/ is no card: no board line, no fold", async () => {
    noteWithEntry("/Work/Board/Old/Pump.md", noteEntry(null), {}, "owner", card(null));
    renderApp("/notes/Work/Board/Old/Pump.md");
    await screen.findByText("Fix the pump.");
    expect(box()).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Properties ·/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "On the board" })).not.toBeInTheDocument();
  });

  it("a board card folds, and its header says In Running since 14 Nov", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-11-20T10:00:00"));
    noteWithEntry(PUMP, noteEntry(null), {}, "owner", card("{ Ready: 2026-11-10, running: 2026-11-14 }"));
    renderApp(`/notes${PUMP}`);
    expect(await screen.findByRole("group", { name: "On the board" })).toHaveTextContent(/^In Running since 14 Nov$/);
    expect(await screen.findByRole("button", { name: "Properties · 3" })).toHaveAttribute("aria-expanded", "false");
    cleanup();

    noteWithEntry(PUMP, noteEntry(null), {}, "owner", card("{ Running: 2025-11-14 }"));
    renderApp(`/notes${PUMP}`);
    expect(await screen.findByRole("group", { name: "On the board" })).toHaveTextContent(/^In Running since 14 Nov 2025$/);
    cleanup();

    noteWithEntry(PUMP, noteEntry(null), {}, "owner", card(null));
    renderApp(`/notes${PUMP}`);
    expect(await screen.findByRole("group", { name: "On the board" })).toHaveTextContent(/^In Running$/);
  });

  it("the board line is Read mode's only", async () => {
    noteWithEntry(PUMP, noteEntry(null), {}, "owner", card(null));
    renderApp(`/notes${PUMP}`);
    await screen.findByRole("group", { name: "On the board" });
    act(() => useEditor.getState().setMode("write"));
    await waitFor(() => expect(screen.queryByRole("group", { name: "On the board" })).not.toBeInTheDocument());
  });

  it("an ordinary note keeps its box, with no toggle", async () => {
    noteWithEntry("/Work/Plan.md", noteEntry(null), {}, "owner", ["---", "tags: [work]", "column: Running", "---", "# Plan", ""].join("\n"));
    renderApp("/notes/Work/Plan.md");
    await screen.findByRole("heading", { name: "Plan" });
    expect(box()).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Properties ·/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "On the board" })).not.toBeInTheDocument();
  });

  it("frontmatter that can't be read never folds", async () => {
    noteWithEntry(ZAHNARZT, dated, {}, "owner", ["---", "when: 2026-11-20", "author:", "  name: Ada", "---", "# Zahnarzt", ""].join("\n"));
    renderApp(`/notes${ZAHNARZT}`);
    await screen.findByRole("group", { name: "In the calendar" });
    expect(document.querySelector("pre[aria-label=Properties]")).toHaveTextContent("name: Ada");
    expect(screen.queryByRole("button", { name: /^Properties ·/ })).not.toBeInTheDocument();
  });

  it("the choice is kept for every such note on this device", async () => {
    noteWithEntry(ZAHNARZT, dated, {}, "owner", calendarNote);
    renderApp(`/notes${ZAHNARZT}`);
    await userEvent.click(await toggle());
    expect(localStorage.getItem("kumbu.propertiesOpen")).toBe("open");
    cleanup();

    noteWithEntry(ZAHNARZT, noteEntry(null), {}, "owner", flagged);
    renderApp(`/notes${ZAHNARZT}`);
    const button = await toggle();
    expect(button).toHaveAttribute("aria-expanded", "true");
    expect(box()).toHaveTextContent("owner");

    await userEvent.click(button);
    expect(localStorage.getItem("kumbu.propertiesOpen")).toBeNull();
    expect(document.querySelector("dl")).toBeNull();
  });

  it("a frontmatter just changed by someone else shows open, with its tint", async () => {
    noteWithEntry(
      ZAHNARZT,
      dated,
      {
        "GET /api/session": () => json(200, { user, previousSignInAt: "2026-10-24T08:00:00Z" }),
        "GET /api/changes": () => json(200, [{ path: ZAHNARZT, writer: "liz", lastWriteAt: new Date().toISOString(), version: "v1" }]),
        [`GET /api/history${ZAHNARZT}`]: () => json(200, [{ version: "v1", writtenAt: new Date().toISOString(), writer: "liz", lines: 7, current: true, changeset: "c1" }]),
      },
      "owner",
      calendarNote,
    );
    renderApp(`/notes${ZAHNARZT}`);
    expect(await toggle()).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(within(await screen.findByRole("region", { name: "Agent changes" })).getByRole("button", { name: "Show" }));

    await waitFor(() => expect(box()).toHaveAttribute("data-changed"));
    expect(box()).toHaveAttribute("data-writer", "liz");
    expect(screen.queryByRole("button", { name: /^Properties ·/ })).not.toBeInTheDocument();
    expect(localStorage.getItem("kumbu.propertiesOpen")).toBeNull();
  });

  it.each([["tags: [{a: b, c: d}, x]"], ["tags: [[a, b], x]"], ["tags: [a,,b]"]])("shows %s as written, not split at its commas", (yaml) => {
    render(
      <MemoryRouter>
        <Properties yaml={yaml} changed={null} />
      </MemoryRouter>,
    );
    expect(document.querySelector("pre[aria-label=Properties]")).toHaveTextContent(yaml);
    expect(box()).toBeNull();
  });

  it("the checkpoint view shows the box as it was", () => {
    render(
      <MemoryRouter>
        <Properties yaml={"when: 2026-11-20\ntags: [flag/important]"} changed={null} still />
      </MemoryRouter>,
    );
    expect(box()).toHaveTextContent("when");
    expect(screen.queryByRole("button", { name: /^Properties ·/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add property" })).not.toBeInTheDocument();
  });
});

describe("+ Add property in the box", () => {
  const PLAN = "/Work/Plan.md";
  const plain = ["---", "tags: [work]", "---", "# Plan", ""].join("\n");
  const add = () => screen.queryByRole("button", { name: "Add property" });

  it("sits at the bottom of the box on a note that doesn't fold, and opens the dialog", async () => {
    noteWithEntry(PLAN, noteEntry(null), {}, "owner", plain);
    renderApp(`/notes${PLAN}`);
    const button = await screen.findByRole("button", { name: "Add property" });
    expect(button).toHaveTextContent("+ Add property");
    expect(button).toHaveClass("min-h-12");
    expect(box()!.nextElementSibling).toBe(button);
    await userEvent.click(button);
    expect(await screen.findByRole("dialog", { name: "Add property" })).toBeInTheDocument();
  });

  it("on a note that folds, only while the box is open", async () => {
    noteWithEntry(ZAHNARZT, dated, {}, "owner", calendarNote);
    renderApp(`/notes${ZAHNARZT}`);
    const button = await toggle();
    expect(add()).not.toBeInTheDocument();
    await userEvent.click(button);
    expect(box()!.nextElementSibling).toBe(await screen.findByRole("button", { name: "Add property" }));
  });

  it("isn't offered without write", async () => {
    noteWithEntry(PLAN, noteEntry(null), {}, "read", plain);
    renderApp(`/notes${PLAN}`);
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeDisabled());
    expect(box()).toBeInTheDocument();
    expect(add()).not.toBeInTheDocument();
  });
});
