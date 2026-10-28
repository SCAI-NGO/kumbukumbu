// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { HistoryEntry } from "../api/notes";
import { lastSeen, pauseText } from "./AgentBanner";

const entry = (version: string, writtenAt: string, writer: string, writerKind: string | null, current = false): HistoryEntry => ({
  version,
  writtenAt,
  writer,
  lines: 1,
  size: 1,
  current,
  changeset: null,
  writerKind,
  kind: "note",
  mediaType: "text/markdown",
});

describe("lastSeen", () => {
  it("lastSeen_counts_a_device_write_as_the_users", () => {
    const since = "2026-10-26T08:00:00Z";
    const history = [
      entry("v3", "2026-10-26T10:00:00Z", "liz", "agent", true),
      entry("v2", "2026-10-26T09:30:00Z", "Work – Obsidian Laptop", "device"),
      entry("v1", "2026-10-26T09:00:00Z", "liz", "agent"),
    ];

    expect(lastSeen(history, since, "ada")).toBe("v2");
  });

  it("goes back past agents' writes to one before the previous sign-in", () => {
    const history = [entry("v2", "2026-10-26T10:00:00Z", "liz", "agent", true), entry("v1", "2026-10-25T10:00:00Z", "liz", "agent")];

    expect(lastSeen(history, "2026-10-26T08:00:00Z", "ada")).toBe("v1");
  });

  it("does not take a device back after a long pause for the user", () => {
    const device = "Work – Obsidian Pixel 9";
    const history = [
      entry("v3", "2026-10-26T10:00:00Z", device, "device", true),
      entry("v2", "2026-10-26T09:50:00Z", `${device} (edit)`, "device"),
      entry("v1", "2026-10-26T09:00:00Z", "Work – Obsidian Laptop", "device"),
    ];
    const change = {
      path: "/Work/plan.md",
      writer: device,
      lastWriteAt: "2026-10-26T10:00:00Z",
      version: "v3",
      writerKind: "device",
      quietSince: "2026-10-01T10:00:00Z",
      resumedAt: "2026-10-26T09:50:00Z",
    };

    expect(lastSeen(history, "2026-10-26T08:00:00Z", "ada", change)).toBe("v1");
    expect(lastSeen(history, "2026-10-26T08:00:00Z", "ada")).toBe("v2");
  });
});

describe("pauseText", () => {
  const day = 86_400_000;
  const since = (days: number) => [new Date(0).toISOString(), new Date(days * day).toISOString()] as const;

  it("says days, then weeks, months and years", () => {
    expect(pauseText(...since(15))).toBe("15 days");
    expect(pauseText(...since(22))).toBe("3 weeks");
    expect(pauseText(...since(70))).toBe("2 months");
    expect(pauseText(...since(400))).toBe("1 year");
  });
});
