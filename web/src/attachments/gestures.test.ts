// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { clampView, fitSize, maxScale, swipeOf, wheelFactor, zoomAt } from "./gestures";

describe("gestures", () => {
  const stage = { width: 400, height: 800 };

  it("fits an image into the stage", () => {
    const tall = fitSize({ width: 1440, height: 3120 }, stage);
    expect(tall.width).toBeCloseTo(1440 * (800 / 3120));
    expect(tall.height).toBeCloseTo(800);
    expect(fitSize({ width: 2000, height: 1000 }, stage)).toEqual({ width: 400, height: 200 });
  });

  it("zooms about a point, keeping what is under it there", () => {
    const view = zoomAt({ scale: 1, x: 0, y: 0 }, 2, { x: 100, y: -50 });
    expect(view).toEqual({ scale: 2, x: -100, y: 50 });
    // The point maps to itself: x + scale × (point − x) / previous scale.
    expect(view.x + 2 * 100).toBe(100);
  });

  it("keeps a zoomed image over the stage and the scale within bounds", () => {
    const fit = { width: 400, height: 200 };
    expect(clampView({ scale: 2, x: 500, y: 300 }, fit, stage)).toEqual({ scale: 2, x: 200, y: 0 });
    expect(clampView({ scale: 0.5, x: 10, y: 10 }, fit, stage)).toEqual({ scale: 1, x: 0, y: 0 });
    expect(clampView({ scale: 20, x: 0, y: 0 }, fit, stage).scale).toBe(maxScale);
  });

  it("zooms gently with a mouse wheel and in proportion with a trackpad", () => {
    const steps = (e: { deltaY: number; deltaMode: number; ctrlKey: boolean }, n: number) => wheelFactor(e) ** n;
    // Five notches, whatever size the browser gives them, land near 230 %; as many back return to fit.
    for (const notch of [{ deltaY: -100, deltaMode: 0 }, { deltaY: -200, deltaMode: 0 }, { deltaY: -3, deltaMode: 1 }]) {
      expect(steps({ ...notch, ctrlKey: false }, 5)).toBeGreaterThan(2);
      expect(steps({ ...notch, ctrlKey: false }, 5)).toBeLessThan(2.5);
      expect(steps({ ...notch, ctrlKey: false }, 5) * steps({ ...notch, deltaY: -notch.deltaY, ctrlKey: false }, 5)).toBeCloseTo(1);
    }
    // Ctrl with a mouse wheel is no faster.
    expect(wheelFactor({ deltaY: -100, deltaMode: 0, ctrlKey: true })).toBeCloseTo(wheelFactor({ deltaY: -100, deltaMode: 0, ctrlKey: false }));
    // A trackpad's pinch: small deltas, each in proportion, so that a pinch of 20 events × 4 px doubles.
    expect(wheelFactor({ deltaY: -4, deltaMode: 0, ctrlKey: true })).toBeCloseTo(Math.exp(0.04));
    expect(steps({ deltaY: -4, deltaMode: 0, ctrlKey: true }, 20)).toBeCloseTo(Math.exp(0.8));  });

  it("tells a swipe sideways or down from a small move", () => {
    expect(swipeOf(-120, 10)).toBe("next");
    expect(swipeOf(120, -10)).toBe("previous");
    expect(swipeOf(10, 150)).toBe("close");
    expect(swipeOf(10, -150)).toBeNull();
    expect(swipeOf(30, 20)).toBeNull();
  });
});
