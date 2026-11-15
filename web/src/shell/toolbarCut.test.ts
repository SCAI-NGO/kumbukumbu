// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { cut, type Tool } from "./toolbarCut";

const all: Tool[] = ["newNote", "upload", "calendar", "board", "recent", "sort", "find", "menu"];
const reading: Tool[] = ["calendar", "board", "recent", "sort", "find", "menu"];
const noBoard: Tool[] = ["newNote", "upload", "calendar", "recent", "sort", "find", "menu"];
/** + New note in IBM Plex Sans, 14 px medium: about 100 px. */
const newNote = 100;

describe("the toolbar's cut by width", () => {
  it.each([
    // The phone's drawer at 375 px: 318 px, less its 1 px border on the right and the row's padding (2 × 8 px).
    [301, all, { shown: ["newNote", "calendar", "board", "find", "menu"], inMenu: ["upload", "sort", "recent"] }],
    // Room for one of the three: Upload goes first.
    [400, all, { shown: ["newNote", "calendar", "board", "recent", "sort", "find", "menu"], inMenu: ["upload"] }],
    // Room for two: Sort goes next, Recent changes stays.
    [360, all, { shown: ["newNote", "calendar", "board", "recent", "find", "menu"], inMenu: ["upload", "sort"] }],
    [600, all, { shown: all, inMenu: [] }],
    [301, noBoard, { shown: ["newNote", "calendar", "recent", "find", "menu"], inMenu: ["upload", "sort"] }],
    [600, noBoard, { shown: noBoard, inMenu: [] }],
    // Without write there is neither New note nor Upload: the room goes to the rest.
    [301, reading, { shown: reading, inMenu: [] }],
    [250, reading, { shown: ["calendar", "board", "recent", "find", "menu"], inMenu: ["sort"] }],
  ])("at %i px", (width, tools, expected) => {
    expect(cut(width, tools, newNote)).toEqual(expected);
  });

  it("keeps everything that fits exactly, and cuts at a pixel less", () => {
    const fits = newNote + 7 * 48;
    expect(cut(fits, all, newNote)).toEqual({ shown: all, inMenu: [] });
    expect(cut(fits - 1, all, newNote)).toEqual({ shown: ["newNote", "calendar", "board", "recent", "sort", "find", "menu"], inMenu: ["upload"] });
  });

  it("then cuts from the right, keeping the order; New note and ⋯ always stay", () => {
    expect(cut(newNote + 3 * 48, all, newNote)).toEqual({ shown: ["newNote", "calendar", "board", "menu"], inMenu: ["upload", "sort", "recent", "find"] });
    expect(cut(newNote + 2 * 48, all, newNote)).toEqual({ shown: ["newNote", "calendar", "menu"], inMenu: ["upload", "sort", "recent", "board", "find"] });
    expect(cut(100, all, newNote)).toEqual({ shown: ["newNote", "menu"], inMenu: ["upload", "sort", "recent", "calendar", "board", "find"] });
  });

  it("shows everything while the width is unknown", () => {
    expect(cut(0, all, newNote)).toEqual({ shown: all, inMenu: [] });
    expect(cut(0, all, 0)).toEqual({ shown: all, inMenu: [] });
  });
});
