// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { useOfflineMode } from "../offline/mode";
import ModeChips from "./ModeChips";

it("offline, Hybrid and Meaning stay, dimmed, with the reason, and the chosen mode is kept", () => {
  render(<ModeChips mode="hybrid" onChange={() => {}} />);
  expect(screen.getByRole("button", { name: "Hybrid" })).toBeEnabled();
  act(() => useOfflineMode.setState({ offline: true }));
  for (const name of ["Hybrid", "Meaning"]) {
    expect(screen.getByRole("button", { name })).toBeDisabled();
    expect(screen.getByRole("button", { name })).toHaveAttribute(
      "title",
      "Needs a connection",
    );
  }
  expect(screen.getByRole("button", { name: "Hybrid" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  expect(screen.getByRole("button", { name: "Exact" })).toBeEnabled();
});
