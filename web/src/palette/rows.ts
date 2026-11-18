// SPDX-License-Identifier: AGPL-3.0-or-later
import { favouriteKey, type Favourite, type FavouriteInput } from "../api/favourites";
import { fileKindOfEntry, type TreeFolder, type TreeView, type VaultView } from "../api/notes";
import { crumbOf, inVault, noteUrl, titleOf } from "../api/paths";
import type { MenuItem } from "../components/Menu";
import { treeHas } from "../shell/lastNote";
import { schemes } from "../theme/schemes";

/**
 * What the palette lists. `group` is its section; `place` is shown muted beside the name and searched with it. A row
 * without `favourite` can't be starred.
 */
export type Row = {
  key: string;
  group: "Note" | "Commands" | "Vaults" | "Notes";
  name: string;
  place: string;
  favourite?: FavouriteInput;
  run: { to: string } | { scheme: string } | { action(): void };
};

const command = (id: string, place: string, name: string, run: Row["run"]): Row => {
  const favourite: FavouriteInput = { kind: "command", id };
  return { key: favouriteKey(favourite), group: "Commands", name, place, favourite, run };
};

const go = (id: string, place: string, name: string, to: string) => command(id, place, name, { to });

/** Follows the settings navigation, the tabs of Access and Skills, and the routes of `router.tsx`. */
export function commandRows({ admin, shared, marketplaces }: { admin: boolean; shared: boolean; marketplaces: boolean }): Row[] {
  return [
    go("settings.appearance", "Settings", "Appearance", "/settings/appearance"),
    go("settings.vaults", "Settings", "Vaults", "/settings/vaults"),
    go("settings.access.keys", "Settings / Access", "API keys", "/settings/access/keys"),
    go("settings.access.apps", "Settings / Access", "Apps", "/settings/access/apps"),
    go("settings.access.dav", "Settings / Access", "WebDAV", "/settings/access/dav"),
    go("settings.skills.installed", "Settings / Skills", "Installed", "/settings/skills/installed"),
    go("settings.skills.try", "Settings / Skills", "Try", "/settings/skills/try"),
    ...(shared ? [go("settings.skills.shared", "Settings / Skills", "Shared", "/settings/skills/shared")] : []),
    ...(marketplaces ? [go("settings.skills.marketplaces", "Settings / Skills", "Marketplaces", "/settings/skills/marketplaces")] : []),
    go("settings.calendar", "Settings", "Calendar", "/settings/calendar"),
    go("settings.security", "Settings", "Security", "/settings/security"),
    go("settings.data", "Settings", "Your data", "/settings/data"),
    go("settings.about", "Settings", "About", "/settings/about"),
    ...(admin ? [go("admin.users", "Admin", "Users", "/admin/users"), go("admin.invitations", "Admin", "Invitations", "/admin/invitations")] : []),
    go("open.recent", "Open", "Recent", "/recent"),
    go("open.whatson", "Open", "What's on", "/whats-on"),
    go("open.console", "Open", "Console", "/console"),
    ...schemes.map((s) => command(`scheme.${s.id}`, "Colour scheme", s.name, { scheme: s.id })),
  ];
}

/** The open note's menu, as rows placed with its title; not pinnable, as they act on the note that is open. */
export function noteActionRows(title: string, items: MenuItem[]): Row[] {
  return items.map((item) => ({ key: `note-action:${item.label}`, group: "Note", name: item.label, place: title, run: { action: item.onSelect } }));
}

export function vaultRows(vaults: VaultView[]): Row[] {
  return vaults.flatMap(({ name }) => {
    const row = (favourite: FavouriteInput, label: string, to: string, where = name): Row => ({ key: favouriteKey(favourite), group: "Vaults", name: label, place: where, favourite, run: { to } });
    const at = encodeURIComponent(name);
    return [
      row({ kind: "vault", vault: name }, name, `/notes/${at}`, "Vault"),
      row({ kind: "board", vault: name }, "Board", `/board/${at}`),
      row({ kind: "calendar", vault: name }, "Calendar", `/calendar/${at}`),
      row({ kind: "gallery", vault: name }, "Gallery", `/gallery/${at}`),
    ];
  });
}

/** The notes below `root`, not attachments and not what `_archive/` holds. */
export function noteRows(vault: string, root: TreeFolder): Row[] {
  const rows: Row[] = [];
  const walk = (folder: TreeFolder) => {
    for (const note of folder.notes) {
      const inside = inVault(note.path);
      if (fileKindOfEntry(note) !== "note" || inside.startsWith("_archive/")) continue;
      const favourite: FavouriteInput = { kind: "note", vault, path: inside };
      rows.push({ key: favouriteKey(favourite), group: "Notes", name: titleOf(note.path), place: crumbOf(note.path), favourite, run: { to: noteUrl(note.path) } });
    }
    folder.folders.forEach(walk);
  };
  walk(root);
  return rows;
}

/** A vault's tree as read, and when (`dataUpdatedAt`). */
export type ReadTree = { tree: TreeView; at: number };

// Allowed for the clocks of two devices: a favourite another device added shortly before the tree was read may be later.
const SKEW = 5 * 60_000;

/**
 * The keys of note favourites whose note was moved, renamed or deleted: those of a vault whose tree is loaded (`trees`)
 * and lacks it, added more than five minutes before the tree was read. A vault without a loaded tree (still loading,
 * failed, not listed: a hidden one's) keeps its entries; so does one whose tree leaves out notes that don't open
 * (`damaged`), as the note may be one of them; and so does one added since, by another device, to a note the tree
 * doesn't have yet.
 */
export function staleNotes(favourites: Favourite[], trees: ReadonlyMap<string, ReadTree>): string[] {
  return favourites.flatMap((f) => {
    const read = f.kind === "note" && f.vault ? trees.get(f.vault) : undefined;
    return read && Number(read.tree.damaged) === 0 && Date.parse(f.added) < read.at - SKEW && !treeHas(read.tree.root, `/${f.vault}/${f.path}`)
      ? [favouriteKey(f)]
      : [];
  });
}
