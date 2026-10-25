// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { swipe } from "./useEdgeSwipe";

const at = (x: number, y: number) => ({ x, y });

describe("the edge swipe", () => {
  it("opens from less than 32 px off the left edge after more than 60 px to the right", () => {
    expect(swipe(at(31, 400), at(92, 420), false)).toBe("open");
    expect(swipe(at(32, 400), at(120, 400), false)).toBeNull();
    expect(swipe(at(10, 400), at(70, 400), false)).toBeNull();
  });

  it("ignores vertical scrolling", () => {
    expect(swipe(at(10, 400), at(100, 460), false)).toBeNull();
    expect(swipe(at(10, 400), at(12, 100), false)).toBeNull();
    expect(swipe(at(300, 400), at(200, 470), true)).toBeNull();
  });

  it("closes an open drawer on a swipe left from anywhere", () => {
    expect(swipe(at(300, 400), at(239, 380), true)).toBe("close");
    expect(swipe(at(300, 400), at(240, 400), true)).toBeNull();
    expect(swipe(at(10, 400), at(100, 400), true)).toBeNull();
  });
});
