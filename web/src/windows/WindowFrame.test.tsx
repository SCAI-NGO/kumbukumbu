// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import WindowFrame from "./WindowFrame";
import type { WindowKind } from "./store";

/** The viewport is as wide as the desktop breakpoint (Tailwind's `lg`) or narrower. */
let desktop = true;
const listeners = new Set<() => void>();
function setDesktop(value: boolean) {
  desktop = value;
  act(() => listeners.forEach((l) => l()));
}

beforeEach(() => {
  desktop = true;
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      return query === "(min-width: 64rem)" ? desktop : false;
    },
    media: query,
    addEventListener: (_: string, l: () => void) => listeners.add(l),
    removeEventListener: (_: string, l: () => void) => listeners.delete(l),
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const Where = () => <p data-testid="where">{useLocation().pathname}</p>;

function renderFrame(kind: WindowKind, heading?: string) {
  render(
    <MemoryRouter initialEntries={["/window"]}>
      <Routes>
        <Route path="/window" element={<WindowFrame kind={kind} title="Settings" heading={heading}>Body</WindowFrame>} />
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
  return screen.getByRole("dialog", { name: "Settings" });
}

/** The dimmed notes under the window: the dialog's aria-hidden sibling. */
const backdrop = (dialog: HTMLElement) => dialog.parentElement!.querySelector<HTMLElement>(":scope > [aria-hidden]")!;

describe("WindowFrame", () => {
  it("robust kinds don't close on a backdrop click; light ones do", async () => {
    for (const kind of ["navigated", "console"] as const) {
      const dialog = renderFrame(kind);
      await userEvent.click(backdrop(dialog));
      expect(screen.getByRole("dialog", { name: "Settings" })).toBeInTheDocument();
      cleanup();
    }
    const dialog = renderFrame("light");
    await userEvent.click(backdrop(dialog));
    expect(screen.getByTestId("where")).toHaveTextContent("/");
  });

  it("the frame marks its kind", () => {
    const sizes = { navigated: ["960", "720"], console: ["1100", "760"], light: ["672", "720"] } as const;
    for (const kind of ["navigated", "console", "light"] as const) {
      const dialog = renderFrame(kind);
      expect(dialog).toHaveAttribute("data-frame", kind === "light" ? "light" : "robust");
      expect(dialog.className).toContain(`lg:w-[min(${sizes[kind][0]}px,100vw-64px)]`);
      expect(dialog.className).toContain(`lg:h-[min(${sizes[kind][1]}px,100dvh-64px)]`);
      expect(backdrop(dialog).className).toContain(kind === "light" ? "bg-black/35" : "bg-black/60");
      cleanup();
    }
  });

  it("on the phone it is a sheet", () => {
    setDesktop(false);
    const dialog = renderFrame("navigated", "Security");
    expect(dialog).toHaveAttribute("data-sheet");
    for (const token of ["absolute", "inset-x-0", "bottom-0", "top-[calc(max(32px,6dvh)+env(safe-area-inset-top))]", "rounded-t-[16px]", "border-b-0", "pb-[env(safe-area-inset-bottom)]", "motion-safe:animate-[k-slide-up_200ms_ease-out]"])
      expect(dialog.classList).toContain(token);
    expect(dialog.classList).not.toContain("relative");
    expect(dialog).toHaveAccessibleName("Settings");
    expect(screen.getByText("· Security")).toBeInTheDocument();
    expect(dialog).toHaveTextContent("Settings· Security");
  });

  it("on the desktop it is no sheet and the title is only the title", () => {
    const dialog = renderFrame("navigated", "Security");
    expect(dialog).not.toHaveAttribute("data-sheet");
    expect(dialog.classList).toContain("relative");
    expect(dialog).not.toHaveTextContent("Security");
  });
});
