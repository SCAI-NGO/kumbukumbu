// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, createEvent, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useRef, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import MarkdownToolbar from "./MarkdownToolbar";

afterEach(cleanup);

function Harness({ initial, onDone = () => {} }: { initial: string; onDone?: () => void }) {
  const [value, setValue] = useState(initial);
  const ref = useRef<HTMLTextAreaElement>(null);
  return (
    <>
      <textarea aria-label="Markdown source" ref={ref} value={value} onChange={(e) => setValue(e.target.value)} />
      <MarkdownToolbar textarea={ref} onChange={setValue} onDone={onDone} />
    </>
  );
}

/** Renders the editor with `text`, focuses it and selects `start`–`end`. */
function editor(text: string, start: number, end = start) {
  render(<Harness initial={text} />);
  const area = screen.getByRole<HTMLTextAreaElement>("textbox", { name: "Markdown source" });
  area.focus();
  area.setSelectionRange(start, end);
  return area;
}

const selection = (area: HTMLTextAreaElement) => area.value.slice(area.selectionStart, area.selectionEnd);

describe("the Markdown toolbar", () => {
  it.each([
    ["Wiki link", "see plan now", 4, 8, "see [[plan]] now"],
    ["Bold", "see plan now", 4, 8, "see **plan** now"],
    ["Inline code", "see plan now", 4, 8, "see `plan` now"],
  ])("%s wraps the selection and keeps it selected", async (name, text, start, end, expected) => {
    const area = editor(text, start, end);
    await userEvent.click(screen.getByRole("button", { name }));
    expect(area).toHaveValue(expected);
    expect(selection(area)).toBe("plan");
    expect(area).toHaveFocus();
  });

  it.each([
    ["Wiki link", "see ", "see [[]]", 6],
    ["Bold", "see ", "see ****", 6],
    ["Inline code", "see ", "see ``", 5],
  ])("%s without a selection inserts the pair with the caret inside", async (name, text, expected, caret) => {
    const area = editor(text, 4);
    await userEvent.click(screen.getByRole("button", { name }));
    expect(area).toHaveValue(expected);
    expect([area.selectionStart, area.selectionEnd]).toEqual([caret, caret]);
    expect(area).toHaveFocus();
  });

  it.each([
    ["Heading", "## "],
    ["Task", "- [ ] "],
    ["List item", "- "],
    ["Quote", "> "],
  ])("%s prefixes the caret's line and moves the caret along", async (name, prefix) => {
    const area = editor("first\nsecond line\nthird", 9);
    await userEvent.click(screen.getByRole("button", { name }));
    expect(area).toHaveValue(`first\n${prefix}second line\nthird`);
    expect(area.selectionStart).toBe(9 + prefix.length);
    expect(area).toHaveFocus();
  });

  it("prefixes every line the selection touches", async () => {
    const area = editor("first\nsecond\nthird", 8, 15);
    await userEvent.click(screen.getByRole("button", { name: "List item" }));
    expect(area).toHaveValue("first\n- second\n- third");
    expect(selection(area)).toBe("cond\n- th");
  });

  it("keeps the focus in the textarea: its buttons cancel pointerdown and mousedown", () => {
    editor("x", 0);
    for (const name of ["Wiki link", "Heading", "Task", "List item", "Bold", "Inline code", "Quote", "Done"]) {
      const button = screen.getByRole("button", { name });
      for (const make of [createEvent.pointerDown, createEvent.mouseDown]) {
        const event = make(button);
        fireEvent(button, event);
        expect(event.defaultPrevented).toBe(true);
      }
    }
  });

  it("has 44 px targets with accessible names, and Done", async () => {
    const onDone = vi.fn();
    render(<Harness initial="" onDone={onDone} />);
    const bar = screen.getByRole("toolbar", { name: "Markdown" });
    for (const button of bar.querySelectorAll("button")) expect(button.className).toMatch(/\b(min-h-11|h-11)\b/);
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(onDone).toHaveBeenCalledOnce();
  });
});
