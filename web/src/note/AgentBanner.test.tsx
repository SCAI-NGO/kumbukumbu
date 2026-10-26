// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { HistoryEntry } from "../api/notes";
import { lastSeen } from "./AgentBanner";

const entry = (version: string, writtenAt: string, writer: string, writerKind: string | null, current = false): HistoryEntry => ({
  version,
  writtenAt,
  writer,
  lines: 1,
  size: 1,
  current,
  changeset: null,
  writerKind,
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
});
