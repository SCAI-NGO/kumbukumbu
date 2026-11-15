// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { cameFromHere, cameFromOf, keptTagOf, keptTagState, withCameFrom, type CameFrom } from "./backLink";

const at = (url: string) => {
  const u = new URL(url, "https://kumbu.test");
  return { pathname: u.pathname, search: u.search, hash: u.hash };
};

describe("where a note was opened from", () => {
  it.each([
    ["/calendar/Work?week=2026-W46&tag=x", { label: "Calendar", to: "/calendar/Work?week=2026-W46&tag=x" }],
    ["/calendar/Work?month=2026-11#couldnt-read", { label: "Calendar", to: "/calendar/Work?month=2026-11#couldnt-read" }],
    ["/whats-on", { label: "What's on", to: "/whats-on" }],
    ["/notes/Work/a.md", null],
    ["/calendarish", null],
  ])("%s", (url, expected) => {
    expect(cameFromHere(at(url))).toEqual(expected);
  });
});

describe("the state that carries it", () => {
  const calendar: CameFrom = { label: "Calendar", to: "/calendar/Work?week=2026-W46" };

  it("keeps the state it joins", () => {
    const state = withCameFrom({ line: 4 }, calendar);
    expect(state).toMatchObject({ line: 4 });
    expect(cameFromOf(state, "/Work/Dentist.md")).toEqual(calendar);
  });

  it("leaves the state as it is without a way back", () => {
    expect(withCameFrom(undefined, null)).toBeUndefined();
    const state = { line: 2 };
    expect(withCameFrom(state, null)).toBe(state);
  });

  it("reads What's on back for a note of any vault", () => {
    const whatsOn: CameFrom = { label: "What's on", to: "/whats-on" };
    expect(cameFromOf(withCameFrom(undefined, whatsOn), "/Home/Trip.md")).toEqual(whatsOn);
  });

  it("reads a calendar back only for a note of its own vault", () => {
    expect(cameFromOf(withCameFrom(undefined, calendar), "/Home/Trip.md")).toBeNull();
  });

  it.each([
    ["another address", { label: "Calendar", to: "https://example.org" }],
    ["another page", { label: "Calendar", to: "/notes/Work/a.md" }],
    ["a label of its own", { label: "Home", to: "/calendar/Work" }],
    ["a calendar under What's on's name", { label: "What's on", to: "/calendar/Work" }],
    ["What's on under the calendar's name", { label: "Calendar", to: "/whats-on" }],
  ])("refuses %s", (_, from) => {
    expect(cameFromOf(withCameFrom(undefined, from as CameFrom), "/Work/a.md")).toBeNull();
  });

  it.each([["a string", "x"], ["null", null], ["undefined", undefined], ["a state without one", { line: 3 }]])("refuses %s", (_, state) => {
    expect(cameFromOf(state, "/Work/a.md")).toBeNull();
  });

  it("refuses a state from another page load: a reload shows the note without it", () => {
    const state = { cameFrom: { label: "What's on", to: "/whats-on", load: "an earlier load" } };
    expect(cameFromOf(state, "/Work/a.md")).toBeNull();
  });
});

describe("a tag kept on What's on only", () => {
  it("goes along with the way back, and comes back with it", () => {
    const from = cameFromHere(at("/whats-on"), "kumbu");
    expect(from).toEqual({ label: "What's on", to: "/whats-on", tag: "kumbu" });
    expect(cameFromOf(withCameFrom(undefined, from), "/Work/a.md")).toEqual(from);
    expect(keptTagOf(keptTagState("kumbu"))).toBe("kumbu");
  });

  it("is left out where the page has none", () => {
    expect(cameFromHere(at("/whats-on"))).toEqual({ label: "What's on", to: "/whats-on" });
  });

  it.each([["another load's", { keptTag: { tag: "kumbu", load: "earlier" } }], ["an empty one", keptTagState("")], ["none", { line: 2 }], ["a string", "x"]])("refuses %s", (_, state) => {
    expect(keptTagOf(state)).toBeUndefined();
  });

  it("refuses a way back whose tag isn't text", () => {
    const state = withCameFrom(undefined, { label: "What's on", to: "/whats-on" }) as { cameFrom: Record<string, unknown> };
    expect(cameFromOf({ cameFrom: { ...state.cameFrom, tag: 3 } }, "/Work/a.md")).toBeNull();
  });
});
