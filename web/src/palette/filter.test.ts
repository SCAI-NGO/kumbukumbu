// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { filterRows } from "./filter";
import type { Row } from "./rows";

const row = (group: Row["group"], place: string, name: string): Row => ({ key: `${group}:${place}/${name}`, group, place, name, favourite: { kind: "command", id: "x" }, run: { to: "/" } });
const none = new Set<string>();
const names = (rows: Row[]) => rows.map((r) => r.name);

describe("filterRows", () => {
  const vaults = row("Commands", "Settings", "Vaults");

  it("finds words in any order across place and name", () => {
    expect(filterRows([vaults, row("Commands", "Settings", "About")], "set vau", none)).toEqual([vaults]);
    const plan = row("Notes", "Work", "plan");
    expect(filterRows([plan], "plan work", none)).toEqual([plan]);
  });

  it("ignores case", () => expect(filterRows([vaults], "SETTINGS vAU", none)).toEqual([vaults]));

  it("needs every word to match", () => expect(filterRows([vaults], "set zzz", none)).toEqual([]));

  it("puts favourites before commands before vaults before notes", () => {
    const n = row("Notes", "Work", "a");
    const v = row("Vaults", "Work", "b");
    const c = row("Commands", "Open", "c");
    const f = row("Notes", "Work", "z");
    expect(names(filterRows([n, v, c, f], "", new Set([f.key])))).toEqual(["z", "c", "b", "a"]);
  });

  it("puts word-start matches first, then alphabetical", () => {
    const rows = [row("Notes", "Work", "Zeta plan"), row("Notes", "Work", "Replan"), row("Notes", "Work", "Plan b"), row("Notes", "Work", "Plan a")];
    expect(names(filterRows(rows, "plan", none))).toEqual(["Plan a", "Plan b", "Zeta plan", "Replan"]);
  });

  it("caps_notes_at_50", () => {
    const rows = Array.from({ length: 1000 }, (_, i) => row("Notes", "Work", `n${i}`));
    expect(filterRows([...rows, vaults], "n", none).filter((r) => r.group === "Notes")).toHaveLength(50);
  });
});
