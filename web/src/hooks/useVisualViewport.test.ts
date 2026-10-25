// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useVisualViewport } from "./useVisualViewport";

afterEach(() => vi.unstubAllGlobals());

/** A visual viewport that the test resizes and scrolls, as an on-screen keyboard does. */
function fakeViewport() {
  const viewport = Object.assign(new EventTarget(), { height: window.innerHeight, offsetTop: 0 });
  vi.stubGlobal("visualViewport", viewport);
  return viewport;
}

describe("useVisualViewport", () => {
  it("answers how far the keyboard covers the bottom of the page, following resize and scroll", () => {
    const viewport = fakeViewport();
    const { result } = renderHook(() => useVisualViewport());
    expect(result.current).toBe(0);

    act(() => {
      viewport.height = window.innerHeight - 300;
      viewport.dispatchEvent(new Event("resize"));
    });
    expect(result.current).toBe(300);

    act(() => {
      viewport.offsetTop = 120;
      viewport.dispatchEvent(new Event("scroll"));
    });
    expect(result.current).toBe(180);
  });

  it("answers 0 without the API", () => {
    vi.stubGlobal("visualViewport", undefined);
    const { result } = renderHook(() => useVisualViewport());
    expect(result.current).toBe(0);
  });
});
