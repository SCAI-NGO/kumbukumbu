// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { useGraphPrefs } from "./useGraphPrefs";

const defaults = { depth: 1, days: true, attachments: true, ghosts: true, sizeBy: "links", preview: true, suggestions: false, list: false };

afterEach(() => localStorage.clear());

describe("useGraphPrefs", () => {
  it("starts with the defaults when nothing is stored", () => {
    const { result } = renderHook(() => useGraphPrefs());
    expect(result.current[0]).toEqual(defaults);
  });

  it("keeps the stored fields and defaults the rest, and any field that isn't valid", () => {
    localStorage.setItem("kumbu.graphPrefs", JSON.stringify({ depth: 3, ghosts: false, sizeBy: "colour", list: "yes" }));
    const { result } = renderHook(() => useGraphPrefs());
    expect(result.current[0]).toEqual({ ...defaults, depth: 3, ghosts: false });
  });

  it("gives the defaults for garbage", () => {
    localStorage.setItem("kumbu.graphPrefs", "{not json");
    const { result } = renderHook(() => useGraphPrefs());
    expect(result.current[0]).toEqual(defaults);
  });

  it("stores a change", () => {
    const { result } = renderHook(() => useGraphPrefs());
    act(() => result.current[1]({ days: false, sizeBy: "length" }));
    expect(result.current[0]).toEqual({ ...defaults, days: false, sizeBy: "length" });
    expect(JSON.parse(localStorage.getItem("kumbu.graphPrefs")!)).toEqual({ ...defaults, days: false, sizeBy: "length" });
  });
});
