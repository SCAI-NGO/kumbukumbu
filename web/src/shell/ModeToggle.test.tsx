// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { useEditor } from "../state/editor";
import ModeToggle from "./ModeToggle";

afterEach(() => {
  cleanup();
  useEditor.getState().reset();
});

describe("ModeToggle", () => {
  it("shows an icon below sm and the word from sm up, named by the word either way", () => {
    render(<ModeToggle writable />);
    for (const name of ["Read", "Write"]) {
      const button = screen.getByRole("button", { name });
      expect(button.querySelector("svg")).toHaveClass("sm:hidden");
      expect(button.querySelector("span")).toHaveTextContent(name);
      expect(button.querySelector("span")).toHaveClass("hidden", "sm:inline");
    }
  });
});
