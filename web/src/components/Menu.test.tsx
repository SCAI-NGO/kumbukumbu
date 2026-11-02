// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Pencil } from "lucide-react";
import { Menu } from "./Menu";
import { useDialog } from "../hooks/useDialog";

const rect = (r: Partial<DOMRect>) => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON() {}, ...r }) as DOMRect;

/** jsdom has no layout: the button sits where the test says, and the menu is 120 high. */
function layout(button: Partial<DOMRect>, viewportHeight = 800) {
  vi.stubGlobal("innerHeight", viewportHeight);
  vi.stubGlobal("innerWidth", 1000);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.getAttribute("aria-haspopup") === "menu") return rect(button);
    if (this.getAttribute("role") === "menu") return rect({ width: 192, height: 120 });
    return rect({});
  });
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const onSelect = vi.fn();
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

  it("Escape, outside click and focus return as before", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    const button = screen.getByRole("button", { name: "Actions" });
    await userEvent.click(button);
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).toBeNull();
    expect(button).toHaveFocus();

    await userEvent.click(button);
    await userEvent.click(document.body.querySelector("[aria-hidden='true'].fixed")!);
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

  it("closes when the page scrolls", async () => {
    layout({ top: 100, bottom: 130, left: 900, right: 940 });
    render(<Clipped />);
    await userEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.scroll(screen.getByTestId("clip"));
    expect(screen.queryByRole("menu")).toBeNull();
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
});
