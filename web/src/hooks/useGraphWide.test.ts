// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useGraphWide, useGraphWideSplit } from "./useGraphWide";

afterEach(() => vi.unstubAllGlobals());

/** A media query list per query, the window `rem` wide, that the test resizes. */
function fakeMedia(rem: number) {
  const lists = new Map<string, EventTarget & { matches: boolean; media: string }>();
  const fits = (query: string) => rem >= Number(/min-width: (\d+)rem/.exec(query)![1]);
  vi.stubGlobal("matchMedia", (query: string) => {
    if (!lists.has(query)) lists.set(query, Object.assign(new EventTarget(), { matches: fits(query), media: query }));
    return lists.get(query)!;
  });
  return (to: number) => {
    rem = to;
    for (const list of lists.values()) {
      list.matches = fits(list.media);
      list.dispatchEvent(new Event("change"));
    }
  };
}

describe.each([
  ["useGraphWide", useGraphWide, 120],
  ["useGraphWideSplit", useGraphWideSplit, 180],
])("%s", (_, hook, from) => {
  it(`is true from ${from}rem on`, () => {
    fakeMedia(from);
    expect(renderHook(() => hook()).result.current).toBe(true);
  });

  it("is false below", () => {
    fakeMedia(from - 1);
    expect(renderHook(() => hook()).result.current).toBe(false);
  });

  it("follows the window as it is resized", () => {
    const resize = fakeMedia(80);
    const { result } = renderHook(() => hook());
    act(() => resize(from));
    expect(result.current).toBe(true);
    act(() => resize(from - 1));
    expect(result.current).toBe(false);
  });
});
