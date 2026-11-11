// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { untilText } from "./snapshot";

describe("untilText", () => {
  it("names the time, in the reader's zone", () => {
    expect(untilText("2026-11-11T20:40:00Z", "UTC")).toBe("20:40");
    expect(untilText("2026-11-11T20:40:00Z", "Asia/Tokyo")).toBe("05:40");
  });
});
