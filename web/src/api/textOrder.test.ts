// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { folderOrder, textOrder, textOrderLocale } from "./textOrder";

describe("textOrder", () => {
  it("orders as the server's TextOrder.Default: no numeric order, case after letters, accents next to their letter", () => {
    // As .NET 10 with ICU sorts them (invariant culture, CompareOptions.None, ordinal tie-break).
    const names = ["img2", "img10", "Banner", "apple", "Apple", "Äpfel", "zebra", "a b", "a/c", "a-b", "a_b", "A"];
    expect([...names].sort(textOrder)).toEqual(["A", "a b", "a_b", "a-b", "a/c", "Äpfel", "apple", "Apple", "Banner", "img10", "img2", "zebra"]);
  });

  it("doesn't follow the runtime's locale, where Ö would come after z", () => {
    expect(textOrderLocale()).toMatch(/^en/);
    // As .NET 10 sorts them with the invariant culture.
    expect(["zebra", "Oz", "Ölberg", "apple", "Äpfel"].sort(textOrder)).toEqual(["Äpfel", "apple", "Ölberg", "Oz", "zebra"]);
  });

  it("orders folders a segment at a time, a folder before those below it", () => {
    const folders = ["/W/a b", "/W/a/c", "/W", "/W/a", "/W/B"];
    expect([...folders].sort(folderOrder)).toEqual(["/W", "/W/a", "/W/a/c", "/W/a b", "/W/B"]);
  });
});
