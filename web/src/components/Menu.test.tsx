// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, render, screen, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Pencil } from "lucide-react";
import { Menu } from "./Menu";
import { useDialog } from "../hooks/useDialog";
import { useOfflineMode } from "../offline/mode";

const rect = (r: Partial<DOMRect>) => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON() {}, ...r }) as DOMRect;

/** jsdom has no layout: the button sits where the test says, and the menu is 120 high unless the test says otherwise. */
function layout(button: Partial<DOMRect>, viewportHeight = 800, menuHeight = 120) {
  vi.stubGlobal("innerHeight", viewportHeight);
  vi.stubGlobal("innerWidth", 1000);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.getAttribute("aria-haspopup") === "menu") return rect(button);
    if (this.getAttribute("role") === "menu") return rect({ width: 192, height: menuHeight });
    return rect({});
  });
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

let onSelect = vi.fn();
beforeEach(() => {
  onSelect = vi.fn();
});
const menu = (placement: "above" | "below" = "below") => (
  <Menu label="Actions" placement={placement} className="b" items={[{ label: "Rename", icon: Pencil, onSelect }]}>
    ⋯
  </Menu>
);

function Clipped({ placement }: { placement?: "above" | "below" }) {
  return (
    <>
      <ul className="overflow-y-auto" data-testid="clip">
        <li>{menu(placement)}</li>
      </ul>
      <button>after</button>
    </>
  );
}

function Dialog({ onClose }: { onClose(): void }) {
  const ref = useDialog<HTMLDivElement>(onClose);
  return (
    <div ref={ref} role="dialog" aria-modal="true" aria-label="Drawer">
      <button>first</button>
      {menu()}
    </div>
  );
}

describe("Menu", () => {
  it("the open menu renders outside its overflow container", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    const items = screen.getByRole("menu");
    expect(within(screen.getByTestId("clip")).queryByRole("menu")).toBeNull();
    expect(items.parentElement).toBe(document.body);
  });

  it("it sits beside its button and turns upward near the bottom", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    const { unmount } = render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    let style = screen.getByRole("menu").style;
    expect(style.top).toBe("134px");
    expect(style.right).toBe("60px");
    unmount();

    layout({ top: 740, bottom: 770, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    style = screen.getByRole("menu").style;
    expect(style.top).toBe("612px"); // 740 - 8 - 120
    expect(style.right).toBe("60px");
  });

  it("an 'above' menu stays above", async () => {
    layout({ top: 500, bottom: 530, left: 900, right: 940 });
    render(<Clipped placement="above" />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    expect(screen.getByRole("menu").style.top).toBe("372px");
  });

  it("a menu taller than the room on its side scrolls within that room", async () => {
    layout({ top: 135, bottom: 165, left: 900, right: 940 }, 800, 900);
    const { unmount } = render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    let items = screen.getByRole("menu");
    expect(items).toHaveClass("overflow-y-auto");
    expect(items.style.top).toBe("169px");
    expect(items.style.maxHeight).toBe("623px"); // 800 - 169 - 8
    unmount();

    // Near the bottom it opens to the side with more room, and starts no higher than the screen's top.
    layout({ top: 700, bottom: 730, left: 900, right: 940 }, 800, 900);
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    items = screen.getByRole("menu");
    expect(items.style.maxHeight).toBe("684px"); // 700 - 8 - 8
    expect(items.style.top).toBe("8px");
  });

  it("a menu that fits keeps its own height as its limit", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    expect(screen.getByRole("menu").style.maxHeight).toBe("658px"); // 800 - 134 - 8
  });

  it("the arrow keys bring the item they reach into view", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    render(
      <Menu label="Actions" placement="below" className="b" items={["One", "Two"].map((label) => ({ label, icon: Pencil, onSelect }))}>
        ⋯
      </Menu>,
    );
    screen.getByRole("button", { name: "Actions" }).focus();
    await userEvent.keyboard("{Enter}");
    await userEvent.tab();
    await userEvent.keyboard("{ArrowDown}");
    expect(screen.getByRole("menuitem", { name: "Two" })).toHaveFocus();
    expect(scrollIntoView.mock.contexts.at(-1)).toBe(screen.getByRole("menuitem", { name: "Two" }));
  });

  it("every item is at least 48 px high, in a section too", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(
      <Menu label="Actions" placement="below" className="b" items={[{ label: "Rename", icon: Pencil, onSelect }]} sections={[{ heading: "Later", items: [{ label: "Postpone", icon: Pencil, onSelect }] }]}>
        ⋯
      </Menu>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    for (const item of screen.getAllByRole("menuitem")) expect(item).toHaveClass("min-h-12");
  });

  it("Escape, outside click and focus return as before", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    const button = screen.getByRole("button", { name: "Actions" });
    await userEvent.click(button);
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).toBeNull();
    expect(button).toHaveFocus();

    await userEvent.click(button);
    await userEvent.click(screen.getByTestId("menu-backdrop"));
    expect(screen.queryByRole("menu")).toBeNull();

    await userEvent.click(button);
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename" }));
    expect(onSelect).toHaveBeenCalledOnce();
    expect(screen.queryByRole("menu")).toBeNull();
    expect(button).toHaveFocus();
  });

  it("the keyboard reaches the items and Tab leaves them", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    const button = screen.getByRole("button", { name: "Actions" });
    button.focus();
    await userEvent.keyboard("{Enter}");
    await userEvent.tab();
    expect(screen.getByRole("menuitem", { name: "Rename" })).toHaveFocus();
    await userEvent.tab();
    expect(screen.queryByRole("menu")).toBeNull();
    expect(screen.getByRole("button", { name: "after" })).toHaveFocus();
  });

  it("closes when the person scrolls with the wheel", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.wheel(screen.getByTestId("clip"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("closes when the person scrolls with a finger", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.touchMove(screen.getByTestId("clip"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("closes when the page scrolls while the person holds a press (a scrollbar)", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.pointerDown(document.documentElement);
    fireEvent.scroll(screen.getByTestId("clip"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("closes when the page scrolls while the person holds a key", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    const button = screen.getByRole("button", { name: "Actions" });
    await userEvent.click(button);
    fireEvent.keyDown(button, { key: "PageDown" });
    fireEvent.scroll(screen.getByTestId("clip"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("stays open and follows its button when a container scrolls by itself", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    layout({ top: 200, bottom: 230, left: 900, right: 940 });
    fireEvent.scroll(screen.getByTestId("clip"));
    expect(screen.getByRole("menu").style.top).toBe("234px");
  });

  it.each([
    ["released", fireEvent.pointerUp],
    ["cancelled", fireEvent.pointerCancel],
  ])("a press that was %s no longer counts: a scroll by itself is followed", async (_, end) => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.pointerDown(document.documentElement);
    end(document.documentElement);
    fireEvent.scroll(screen.getByTestId("clip"));
    expect(screen.getByRole("menu")).toBeInTheDocument();
  });

  it("a key that was released no longer counts: a scroll by itself is followed", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    const button = screen.getByRole("button", { name: "Actions" });
    await userEvent.click(button);
    fireEvent.keyDown(button, { key: "PageDown" });
    fireEvent.keyUp(button, { key: "PageDown" });
    fireEvent.scroll(screen.getByTestId("clip"));
    expect(screen.getByRole("menu")).toBeInTheDocument();
  });

  it("a wheel inside the list keeps it open", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.wheel(screen.getByRole("menu"));
    expect(screen.getByRole("menu")).toBeInTheDocument();
  });

  it("follows the button when the window is resized", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    layout({ top: 200, bottom: 230, left: 900, right: 940 });
    fireEvent(window, new Event("resize"));
    expect(screen.getByRole("menu").style.top).toBe("234px");
  });

  describe("inside an aria-modal dialog", () => {
    it("inside an aria-modal dialog it is portalled into that dialog, outside its scrolling list", async () => {
      layout({ top: 100, bottom: 130, left: 900, right: 940 });
      render(
        <div role="dialog" aria-modal="true" aria-label="Drawer">
          <ul className="overflow-y-auto" data-testid="clip">
            <li>{menu()}</li>
          </ul>
        </div>,
      );
      await userEvent.click(screen.getByRole("button", { name: "Actions" }));
      const open = screen.getByRole("menu");
      expect(screen.getByRole("dialog").contains(open)).toBe(true);
      expect(screen.getByTestId("clip").contains(open)).toBe(false);
    });

    it("Tab out of the button without entering the list closes it", async () => {
      layout({ top: 100, bottom: 130, left: 900, right: 940 });
      render(<Clipped />);
      const button = screen.getByRole("button", { name: "Actions" });
      await userEvent.click(button);
      await userEvent.tab({ shift: true });
      expect(screen.queryByRole("menu")).toBeNull();
    });

    it("Escape closes the menu first and the dialog second; Tab stays with the menu", async () => {
      layout({ top: 100, bottom: 130, left: 900, right: 940 });
      const onClose = vi.fn();
      render(<Dialog onClose={onClose} />);
      const button = screen.getByRole("button", { name: "Actions" });
      await userEvent.click(button);
      await userEvent.tab();
      const item = screen.getByRole("menuitem", { name: "Rename" });
      expect(item).toHaveFocus();
      await userEvent.keyboard("{Escape}");
      expect(screen.queryByRole("menu")).toBeNull();
      expect(onClose).not.toHaveBeenCalled();
      expect(button).toHaveFocus();
      await userEvent.keyboard("{Escape}");
      expect(onClose).toHaveBeenCalledOnce();
    });

    it("Tab past the last item goes back into the dialog's own order, not out to the page", async () => {
      layout({ top: 100, bottom: 130, left: 900, right: 940 });
      render(<Dialog onClose={() => {}} />);
      const button = screen.getByRole("button", { name: "Actions" });
      await userEvent.click(button);
      await userEvent.tab();
      await userEvent.tab();
      // The button is the dialog's last control, so its trap wraps to the first.
      expect(screen.queryByRole("menu")).toBeNull();
      expect(screen.getByRole("button", { name: "first" })).toHaveFocus();
    });

    it("Shift+Tab from the first item returns to the button", async () => {
      layout({ top: 100, bottom: 130, left: 900, right: 940 });
      render(<Dialog onClose={() => {}} />);
      const button = screen.getByRole("button", { name: "Actions" });
      await userEvent.click(button);
      await userEvent.tab();
      await userEvent.tab({ shift: true });
      expect(button).toHaveFocus();
    });
  });

  it("offline, an item that needs a connection stays, dimmed, with the reason as its second line", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    const items = [
      { label: "History", icon: Pencil, onSelect, needsConnection: true },
      { label: "Find in note", icon: Pencil, onSelect },
    ];
    render(
      <Menu label="Actions" placement="below" className="b" items={items}>
        ⋯
      </Menu>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    const history = screen.getByRole("menuitem", { name: /^History/ });
    expect(history).toBeEnabled();
    expect(history).not.toHaveTextContent("Needs a connection");

    act(() => useOfflineMode.setState({ offline: true }));
    expect(history).toBeDisabled();
    expect(history).toHaveTextContent("Needs a connection");
    expect(screen.getByRole("menuitem", { name: "Find in note" })).toBeEnabled();
    fireEvent.click(history);
    expect(onSelect).not.toHaveBeenCalled();
  });
});
