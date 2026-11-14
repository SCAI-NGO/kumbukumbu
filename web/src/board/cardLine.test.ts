// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { parseProperties } from "../note/properties";
import { boardCard, sinceText } from "./cardLine";

describe("boardCard", () => {
  it("a note under Board/ with a column is a card", () => {
    expect(boardCard("/Work/Board/Pump.md", parseProperties("column: Running\nrank: a0"))).toEqual({ column: "Running", since: null });
  });

  it("Board.md and a card under Board/Archive/ are not", () => {
    expect(boardCard("/Work/Board/Board.md", parseProperties("column: Running"))).toBeNull();
    expect(boardCard("/Work/Board/Archive/Pump.md", parseProperties("column: Done"))).toBeNull();
  });

  it("a note in another sub-folder of Board/ is not", () => {
    expect(boardCard("/Work/Board/Old/Pump.md", parseProperties("column: Running"))).toBeNull();
  });

  it("a note elsewhere with a column is not", () => {
    expect(boardCard("/Work/projects/Pump.md", parseProperties("column: Running"))).toBeNull();
    expect(boardCard("/Home/Board/Pump.md", parseProperties("rank: a0"))).toBeNull();
    expect(boardCard("/Work/Board/Pump.md", null)).toBeNull();
  });

  it("the day is the moved entry of its column, ignoring case", () => {
    expect(boardCard("/Work/Board/Pump.md", parseProperties("column: running\nmoved: { Ready: 2026-11-10, Running: 2026-11-14 }"))).toEqual({
      column: "running",
      since: "2026-11-14",
    });
    expect(boardCard("/Work/Board/Pump.md", parseProperties("column: Done\nmoved: { done: 2026-11-15T08:00:00 }"))?.since).toBe("2026-11-15");
  });

  it("no matching entry, no day", () => {
    expect(boardCard("/Work/Board/Pump.md", parseProperties("column: Running\nmoved: { Ready: 2026-11-10 }"))?.since).toBeNull();
    expect(boardCard("/Work/Board/Pump.md", parseProperties("column: Running\nmoved: { Running: soon }"))?.since).toBeNull();
    expect(boardCard("/Work/Board/Pump.md", parseProperties("column: Running\nmoved: yesterday"))?.since).toBeNull();
  });

  it("sinceText adds the year only when it isn't this year", () => {
    expect(sinceText("2026-11-14", "2026-11-20")).toBe("14 Nov");
    expect(sinceText("2025-11-14", "2026-11-20")).toBe("14 Nov 2025");
  });
});
