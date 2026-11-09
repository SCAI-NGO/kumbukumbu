// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useCopied } from "./useCopied";

function Probe() {
  const { copy, status } = useCopied();
  return (
    <>
      <button type="button" onClick={() => copy("x")}>
        go
      </button>
      {status}
    </>
  );
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const region = () => document.querySelector("[aria-live]")!;
const press = () => act(async () => screen.getByRole("button", { name: "go" }).click());

it("keeps the live region in the page, and shows a second copy for its full time", async () => {
  vi.stubGlobal("navigator", { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
  render(<Probe />);
  expect(region()).toHaveTextContent("");
  await press();
  expect(region()).toHaveTextContent("Copied");
  await act(() => vi.advanceTimersByTimeAsync(2500));
  await press();
  await act(() => vi.advanceTimersByTimeAsync(2500));
  expect(region()).toHaveTextContent("Copied");
  await act(() => vi.advanceTimersByTimeAsync(600));
  expect(region()).toHaveTextContent("");
});

it("says so when the clipboard refuses", async () => {
  vi.stubGlobal("navigator", { clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } });
  render(<Probe />);
  await press();
  expect(region()).toHaveTextContent("Couldn't copy. Copy it by hand.");
});
