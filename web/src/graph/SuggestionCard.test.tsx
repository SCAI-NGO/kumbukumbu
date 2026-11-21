// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
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
    const dialog = vi.fn();
    document.addEventListener("keydown", dialog);
    const onClose = show();
    await userEvent.keyboard("{Escape}");
    document.removeEventListener("keydown", dialog);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(dialog).not.toHaveBeenCalled();
  });

  it("closes on a click outside, not on one inside", async () => {
    const onClose = show();
    await userEvent.click(screen.getByText("Similar in meaning, not linked"));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "elsewhere" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
