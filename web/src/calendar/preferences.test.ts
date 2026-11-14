// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { browserWeekend, defaultDays } from "./preferences";

afterEach(() => {
  vi.restoreAllMocks();
});

/** A browser whose `Intl.Locale` answers only with `info`. */
const localeWith = (info: object) => vi.spyOn(Intl, "Locale").mockImplementation((() => info) as unknown as () => Intl.Locale);

describe("the browser's weekend", () => {
  it("en-GB's weekend is Saturday and Sunday", () => {
    expect(browserWeekend("en-GB")).toEqual([6, 7]);
  });

  it("he-IL's weekend is Friday and Saturday", () => {
    expect(browserWeekend("he-IL")).toEqual([5, 6]);
  });

  it("without getWeekInfo it reads weekInfo", () => {
    localeWith({ weekInfo: { weekend: [5] } });
    expect(browserWeekend("ar-DZ")).toEqual([5]);
  });

  it("without either it is Saturday and Sunday", () => {
    localeWith({});
    expect(browserWeekend("he-IL")).toEqual([6, 7]);
  });

  it("an odd language tag falls back to Saturday and Sunday", () => {
    expect(browserWeekend("not a language")).toEqual([6, 7]);
  });

  it("defaultDays marks the weekend off and the rest workdays", () => {
    expect(defaultDays("he-IL")).toEqual(["workday", "workday", "workday", "workday", "off", "off", "workday"]);
    expect(defaultDays("en-GB")).toEqual(["workday", "workday", "workday", "workday", "workday", "off", "off"]);
  });
});
