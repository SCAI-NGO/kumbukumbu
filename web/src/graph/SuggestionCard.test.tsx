// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useDialog } from "../hooks/useDialog";
import SuggestionCard from "./SuggestionCard";

afterEach(cleanup);

const suggestion = { path: "/Work/Plan.md", title: "Plan", similarity: { score: 0.7, measured: false } };

function show() {
  const onClose = vi.fn();
  render(
    <div>
      <button type="button">elsewhere</button>
      <SuggestionCard suggestion={suggestion} onClose={onClose} />
    </div>,
  );
  return onClose;
}

describe("SuggestionCard", () => {
  it("names the note and offers both actions, disabled until link prediction comes", () => {
    show();
    const card = screen.getByRole("dialog", { name: "Plan" });
    expect(card).toHaveTextContent("Similar in meaning, not linked");
    expect(card).toHaveTextContent("Coming with link prediction");
    for (const name of ["Add link", "Not related"]) {
      const button = screen.getByRole("button", { name });
      expect(button).toBeDisabled();
      expect(button).toHaveAttribute("title", "Coming with link prediction");
    }
  });

  it("closes on Escape", async () => {
    const onClose = show();
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("keeps its Escape from the dialog it is in, so that one closes only the card", async () => {
    const onClose = vi.fn();
    const closeDialog = vi.fn();
    function Holder() {
      const ref = useDialog<HTMLDivElement>(closeDialog);
      return (
        <div ref={ref} role="dialog" aria-label="Graph">
          <SuggestionCard suggestion={suggestion} onClose={onClose} />
        </div>
      );
    }
    render(<Holder />);
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(closeDialog).not.toHaveBeenCalled();
  });

  it("with no dialog around it, closes on Escape and lets the Escape go on (to the editor's own)", async () => {
    const reached = vi.fn();
    document.addEventListener("keydown", reached);
    try {
      const onClose = show();
      await userEvent.keyboard("{Escape}");
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(reached).toHaveBeenCalledTimes(1);
      expect(reached.mock.calls[0][0].defaultPrevented).toBe(false);
    } finally {
      document.removeEventListener("keydown", reached);
    }
  });

  it("leaves Escape to a dialog opened above it", async () => {
    const onClose = show();
    const closeDialog = vi.fn();
    function Above() {
      const ref = useDialog<HTMLDivElement>(closeDialog);
      return <div ref={ref} role="dialog" aria-label="Above" />;
    }
    render(<Above />);
    await userEvent.keyboard("{Escape}");
    expect(closeDialog).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();
  });

  it("closes on a click outside, not on one inside", async () => {
    const onClose = show();
    await userEvent.click(screen.getByText("Similar in meaning, not linked"));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "elsewhere" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
