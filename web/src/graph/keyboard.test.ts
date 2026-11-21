// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { nearestInDirection } from "./keyboard";

const origin = { x: 0, y: 0 };

describe("nearestInDirection", () => {
  it("picks the closer of two nodes to the right", () => {
    expect(
      nearestInDirection(
        origin,
        [
          { key: "far", x: 100, y: 0 },
          { key: "near", x: 50, y: 10 },
        ],
        "right",
      ),
    ).toBe("near");
  });

  it("ignores a node up and to the right at 60° from the axis", () => {
    const at60 = { key: "steep", x: 20 * Math.cos(Math.PI / 3), y: -20 * Math.sin(Math.PI / 3) };
    expect(nearestInDirection(origin, [at60, { key: "flat", x: 90, y: 5 }], "right")).toBe("flat");
    expect(nearestInDirection(origin, [at60], "right")).toBeNull();
  });

  it("answers null with nothing to the left", () => {
    expect(nearestInDirection(origin, [{ key: "a", x: 40, y: 0 }, { key: "b", x: 0, y: 40 }], "left")).toBeNull();
  });

  it("takes up as towards smaller y, as on the screen", () => {
    expect(nearestInDirection(origin, [{ key: "below", x: 0, y: 30 }, { key: "above", x: 0, y: -60 }], "up")).toBe("above");
    expect(nearestInDirection(origin, [{ key: "below", x: 0, y: 30 }, { key: "above", x: 0, y: -60 }], "down")).toBe("below");
  });
});
