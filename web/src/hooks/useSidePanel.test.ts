// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSidePanelCollapsed, useSidePanelTab } from "./useSidePanel";

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe("side panel state", () => {
  it("starts expanded, on Links", () => {
    expect(renderHook(() => useSidePanelCollapsed()).result.current[0]).toBe(false);
    expect(renderHook(() => useSidePanelTab()).result.current[0]).toBe("links");
  });

  it("remembers collapsed and the tab across a reload", () => {
    const collapsed = renderHook(() => useSidePanelCollapsed());
    const tab = renderHook(() => useSidePanelTab());
    act(() => collapsed.result.current[1](true));
    act(() => tab.result.current[1]("outline"));
    expect(collapsed.result.current[0]).toBe(true);
    expect(tab.result.current[0]).toBe("outline");
    collapsed.unmount();
    tab.unmount();

    expect(renderHook(() => useSidePanelCollapsed()).result.current[0]).toBe(true);
    expect(renderHook(() => useSidePanelTab()).result.current[0]).toBe("outline");
  });

  it("forgets collapsed when expanded again", () => {
    const { result } = renderHook(() => useSidePanelCollapsed());
    act(() => result.current[1](true));
    act(() => result.current[1](false));
    expect(renderHook(() => useSidePanelCollapsed()).result.current[0]).toBe(false);
  });

  it("takes an unknown stored tab for Links", () => {
    localStorage.setItem("kumbu.sidePanelTab", "graph");
    expect(renderHook(() => useSidePanelTab()).result.current[0]).toBe("links");
  });

  it("falls back to the defaults where localStorage throws, and still holds for the visit", () => {
    const fail = () => {
      throw new Error("blocked");
    };
    vi.stubGlobal("localStorage", { getItem: fail, setItem: fail, removeItem: fail });
    const collapsed = renderHook(() => useSidePanelCollapsed());
    const tab = renderHook(() => useSidePanelTab());
    expect(collapsed.result.current[0]).toBe(false);
    expect(tab.result.current[0]).toBe("links");
    act(() => collapsed.result.current[1](true));
    act(() => tab.result.current[1]("outline"));
    expect(collapsed.result.current[0]).toBe(true);
    expect(tab.result.current[0]).toBe("outline");
  });
});
