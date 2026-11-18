// SPDX-License-Identifier: AGPL-3.0-or-later
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { History, Pencil } from "lucide-react";
import { PointMenu } from "./PointMenu";

const rect = (r: Partial<DOMRect>) => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON() {}, ...r }) as DOMRect;

/** jsdom has no layout: the viewport is 1000 × 800 and the list 192 × `height`. */
function layout(height = 120) {
  vi.stubGlobal("innerHeight", 800);
  vi.stubGlobal("innerWidth", 1000);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    return this.getAttribute("role") === "menu" ? rect({ width: 192, height }) : rect({});
  });
}

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
});
