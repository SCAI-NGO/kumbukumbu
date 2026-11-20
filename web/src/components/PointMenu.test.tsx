// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { History, Pencil } from "lucide-react";
import { PointMenu } from "./PointMenu";

const rect = (r: Partial<DOMRect>) => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON() {}, ...r }) as DOMRect;

/** jsdom has no layout: the viewport is 1000 × 800 and the list 192 × `height` (of the list, given as a function). */
function layout(height: number | ((list: HTMLElement) => number) = 120) {
  vi.stubGlobal("innerHeight", 800);
  vi.stubGlobal("innerWidth", 1000);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.getAttribute("role") !== "menu") return rect({});
    return rect({ width: 192, height: typeof height === "number" ? height : height(this) });
  });
}

/** 36 px an item: jsdom's scroll height is 0, so the list's height follows its items. */
const byItems = (list: HTMLElement) => 36 * list.querySelectorAll("[role=menuitem]").length;

const many = (n: number) => Array.from({ length: n }, (_, i) => ({ label: `Item ${i + 1}`, icon: Pencil, onSelect: vi.fn() }));

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const items = (onSelect = vi.fn()) => [
  { label: "Rename", icon: Pencil, onSelect },
  { label: "History", icon: History, onSelect: vi.fn() },
];

describe("PointMenu", () => {
  it("opens_below_and_right_of_its_point_with_the_focus_on_the_first_item", () => {
    layout();
    render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 200 }} onClose={vi.fn()} />);
    const menu = screen.getByRole("menu", { name: "Note actions" });
    expect(menu.style.left).toBe("300px");
    expect(menu.style.top).toBe("204px");
    expect(screen.getByRole("menuitem", { name: "Rename" })).toHaveFocus();
  });

  it("opens_below_the_bottom_of_a_line_and_above_its_top", () => {
    layout();
    const { unmount } = render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 200, bottom: 226 }} onClose={vi.fn()} />);
    expect(screen.getByRole("menu").style.top).toBe("230px");
    unmount();
    render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 740, bottom: 766 }} onClose={vi.fn()} />);
    expect(screen.getByRole("menu").style.top).toBe(`${740 - 4 - 120}px`);
  });

  it("point_menu_flips_where_there_is_no_room", () => {
    layout();
    render(<PointMenu label="Note actions" items={items()} at={{ left: 900, top: 750 }} onClose={vi.fn()} />);
    const menu = screen.getByRole("menu");
    // Above the point, and ending at it.
    expect(menu.style.top).toBe(`${750 - 4 - 120}px`);
    expect(menu.style.left).toBe(`${900 - 192}px`);
  });

  it("ends_at_the_point_with_align_end", () => {
    layout();
    const { unmount } = render(<PointMenu label="Note actions" items={items()} at={{ left: 600, top: 500, align: "end" }} onClose={vi.fn()} />);
    // Room on the left: it ends at the point, as a menu at a button's right edge does.
    expect(screen.getByRole("menu").style.left).toBe(`${600 - 192}px`);
    unmount();
    // No room on the left: from the point rightwards, within the window.
    render(<PointMenu label="Note actions" items={items()} at={{ left: 100, top: 500, align: "end" }} onClose={vi.fn()} />);
    expect(screen.getByRole("menu").style.left).toBe("100px");
  });

  it("scrolls_within_the_side_with_more_room_where_it_fits_on_neither", () => {
    layout(900);
    render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 300 }} onClose={vi.fn()} />);
    const menu = screen.getByRole("menu");
    expect(menu.style.top).toBe("304px");
    expect(menu.style.maxHeight).toBe(`${800 - 304 - 8}px`);
  });

  it("escape_and_a_click_outside_close_it", async () => {
    layout();
    const onClose = vi.fn();
    const { unmount } = render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 200 }} onClose={onClose} />);
    const escape = fireEvent.keyDown(screen.getByRole("menuitem", { name: "Rename" }), { key: "Escape" });
    expect(escape).toBe(false);
    expect(onClose).toHaveBeenCalledTimes(1);
    unmount();
    render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 200 }} onClose={onClose} />);
    await userEvent.click(screen.getByTestId("menu-backdrop"));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("arrows_move_between_the_items_and_a_choice_closes_it_first", async () => {
    layout();
    const order: string[] = [];
    const onSelect = vi.fn(() => order.push("select"));
    render(<PointMenu label="Note actions" items={items(onSelect)} at={{ left: 300, top: 200 }} onClose={() => order.push("close")} />);
    await userEvent.keyboard("{ArrowDown}");
    expect(screen.getByRole("menuitem", { name: "History" })).toHaveFocus();
    await userEvent.keyboard("{ArrowUp}{Enter}");
    expect(order).toEqual(["close", "select"]);
  });

  it("stays_on_screen_for_a_point_out_of_view", () => {
    layout();
    // A caret scrolled out of the editor's view, below the window and left of it.
    render(<PointMenu label="Note actions" items={items()} at={{ left: -50, top: 2000 }} onClose={vi.fn()} />);
    const menu = screen.getByRole("menu");
    expect(menu.style.top).toBe(`${800 - 8 - 120}px`);
    expect(menu.style.left).toBe("8px");
  });

  it("is_placed_again_when_its_items_change_and_keeps_the_focus_where_it_is", async () => {
    layout(byItems);
    const at = { left: 300, top: 700 };
    const { rerender } = render(<PointMenu label="Note actions" items={many(2)} at={at} onClose={vi.fn()} />);
    const menu = screen.getByRole("menu");
    expect(menu.style.top).toBe("704px");
    // Twelve items, 432 px: no room below, so above the point, ending at it.
    rerender(<PointMenu label="Note actions" items={many(12)} at={at} onClose={vi.fn()} />);
    expect(menu.style.top).toBe(`${700 - 4 - 432}px`);
    await userEvent.keyboard("{ArrowDown}");
    expect(screen.getByRole("menuitem", { name: "Item 2" })).toHaveFocus();
    // Placed again, taller: the focus stays on the second item.
    rerender(<PointMenu label="Note actions" items={many(13)} at={at} onClose={vi.fn()} />);
    expect(menu.style.top).toBe(`${700 - 4 - 468}px`);
    expect(screen.getByRole("menuitem", { name: "Item 2" })).toHaveFocus();
  });

  it("is_placed_again_when_its_size_changes", () => {
    // jsdom has no ResizeObserver: this one reports a change in size when asked.
    let resized = () => {};
    vi.stubGlobal(
      "ResizeObserver",
      class {
        constructor(callback: () => void) {
          resized = callback;
        }
        observe() {}
        disconnect() {
          resized = () => {};
        }
      },
    );
    let height = 72;
    layout(() => height);
    render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 700 }} onClose={vi.fn()} />);
    const menu = screen.getByRole("menu");
    expect(menu.style.top).toBe("704px");
    height = 432;
    act(() => resized());
    expect(menu.style.top).toBe(`${700 - 4 - 432}px`);
  });

  it("focuses_the_first_item_only_once_it_is_visible", () => {
    layout();
    // A browser doesn't focus a hidden element, jsdom does: record whether the list was hidden at each focus.
    const focus = HTMLElement.prototype.focus;
    const hidden: [string, boolean][] = [];
    vi.spyOn(HTMLElement.prototype, "focus").mockImplementation(function (this: HTMLElement, options?: FocusOptions) {
      hidden.push([this.textContent ?? "", this.closest<HTMLElement>("[role=menu]")?.style.visibility === "hidden"]);
      focus.call(this, options);
    });
    render(<PointMenu label="Note actions" items={items()} at={{ left: 300, top: 200 }} onClose={vi.fn()} />);
    expect(screen.getByRole("menuitem", { name: "Rename" })).toHaveFocus();
    expect(hidden).toEqual([["Rename", false]]);
  });
});
