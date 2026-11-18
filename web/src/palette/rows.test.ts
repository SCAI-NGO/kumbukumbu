// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { TreeFolder, VaultView } from "../api/notes";
import { schemes } from "../theme/schemes";
import { commandRows, noteRows, vaultRows } from "./rows";

const note = (path: string, kind = "note") => ({ name: path.split("/").pop()!, path, size: 1, kind, mediaType: "text/markdown" });
const folder = (path: string, notes: ReturnType<typeof note>[], folders: TreeFolder[] = []): TreeFolder => ({ name: path.split("/").pop()!, path, notes, folders });

const off = { shared: false, marketplaces: false };

describe("commandRows", () => {
  it("offers the Skills tabs Shared and Marketplaces only where they are enabled", () => {
    const keys = (o: typeof off) => commandRows({ admin: false, ...o }).map((r) => r.key);
    expect(keys(off)).not.toContain("command:settings.skills.shared");
    expect(keys(off)).not.toContain("command:settings.skills.marketplaces");
    expect(keys({ shared: true, marketplaces: false })).toContain("command:settings.skills.shared");
    expect(keys({ shared: false, marketplaces: true })).toContain("command:settings.skills.marketplaces");
  });

  it("gives admin rows only to admins", () => {
    expect(commandRows({ ...off, admin: false }).some((r) => r.key.startsWith("command:admin."))).toBe(false);
    expect(commandRows({ ...off, admin: true }).map((r) => r.key)).toContain("command:admin.users");
  });

  it("has one colour-scheme row per scheme", () => {
    const rows = commandRows({ ...off, admin: false }).filter((r) => r.place === "Colour scheme");
    expect(rows.map((r) => r.run)).toEqual(schemes.map((s) => ({ scheme: s.id })));
    expect(rows[0].key).toBe(`command:scheme.${schemes[0].id}`);
  });

  it("splits a command into its group and name", () => {
    expect(commandRows({ ...off, admin: false }).find((r) => r.key === "command:settings.vaults")).toMatchObject({ group: "Commands", place: "Settings", name: "Vaults", run: { to: "/settings/vaults" } });
  });
});

describe("vaultRows", () => {
  it("lists the vault, its board, calendar and gallery", () => {
    const rows = vaultRows([{ name: "Work" } as VaultView]);
    expect(rows.map((r) => r.key)).toEqual(["vault:Work", "board:Work", "calendar:Work", "gallery:Work"]);
    expect(rows.map((r) => r.place)).toEqual(["Vault", "Work", "Work", "Work"]);
    expect(rows[1].run).toEqual({ to: "/board/Work" });
  });
});

describe("noteRows", () => {
  it("lists notes only, none archived, with their place and favourite path inside the vault", () => {
    const root = folder("/Work", [note("/Work/plan.md"), note("/Work/shot.png", "image")], [
      folder("/Work/projects", [note("/Work/projects/Big plan.md")]),
      folder("/Work/_archive", [note("/Work/_archive/old.md")]),
    ]);
    const rows = noteRows("Work", root);
    expect(rows.map((r) => [r.name, r.place])).toEqual([["plan", "Work"], ["Big plan", "Work / projects"]]);
    expect(rows[1].favourite).toEqual({ kind: "note", vault: "Work", path: "projects/Big plan.md" });
    expect(rows[1].key).toBe("note:Work/projects/Big plan.md");
    expect(rows[1].run).toEqual({ to: "/notes/Work/projects/Big%20plan.md" });
  });
});
