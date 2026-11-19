// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useWide } from "./useWide";

afterEach(() => vi.unstubAllGlobals());

/** A media query list per query that the test switches, as resizing the window does. */
function fakeMedia(wide: boolean) {
  const lists = new Map<string, EventTarget & { matches: boolean; media: string }>();
  vi.stubGlobal("matchMedia", (query: string) => {
    if (!lists.has(query)) lists.set(query, Object.assign(new EventTarget(), { matches: query === "(min-width: 80rem)" && wide, media: query }));
    return lists.get(query)!;
  });
  return (to: boolean) => {
    const list = lists.get("(min-width: 80rem)")!;
    list.matches = to;
    list.dispatchEvent(new Event("change"));
  };
}

describe("useWide", () => {
  it("is true from 80rem on", () => {
    fakeMedia(true);
    expect(renderHook(() => useWide()).result.current).toBe(true);
  });

  it("is false below", () => {
    fakeMedia(false);
    expect(renderHook(() => useWide()).result.current).toBe(false);
  });

  it("is false without matchMedia", () => {
    vi.stubGlobal("matchMedia", undefined);
    expect(renderHook(() => useWide()).result.current).toBe(false);
  });

  it("follows the window as it is resized", () => {
    const resize = fakeMedia(false);
    const { result } = renderHook(() => useWide());
    act(() => resize(true));
    expect(result.current).toBe(true);
    act(() => resize(false));
    expect(result.current).toBe(false);
  });
});
