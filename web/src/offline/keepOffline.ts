// SPDX-License-Identifier: AGPL-3.0-or-later
// "Keep offline" in the tree's folder and vault menus (design §3a, "Settings, Offline" item 4; §6): every attachment
// below the folder stays on this device, whatever its size. Set where the folder is; Settings lists them.
import { isKept, mirrors } from "eidolon-core/store/choice";
import { CloudDownload, CloudOff } from "lucide-react";
import { useReducer } from "react";
import type { MenuItem } from "../components/Menu";
import { useOfflineCopy } from "./copy";
import { chooseOffline } from "./engines";

/**
 * The menu item for a folder (`folder`: its path in the vault, "" the vault itself) of the vault `vaultId`, or none:
 * without an open copy, or for a vault this device doesn't keep. Inside a folder kept whole, only that one is offered.
 */
export function useKeepOffline(vaultId: string | undefined): (folder: string) => MenuItem[] {
  const mirror = useOfflineCopy((s) => s.mirror);
  const [, changed] = useReducer((n: number) => n + 1, 0);
  return (folder) => {
    if (vaultId === undefined || mirror === null || mirror.locked || !mirrors(mirror.choice, vaultId)) return [];
    const kept = mirror.choice.kept[vaultId] ?? [];
    if (kept.includes(folder)) {
      const remove = () => void chooseOffline(mirror, (c) => ({ ...c, kept: { ...c.kept, [vaultId]: (c.kept[vaultId] ?? []).filter((f) => f !== folder) } })).then(changed);
      return [{ label: "Don't keep offline", icon: CloudOff, onSelect: remove }];
    }
    if (folder !== "" && isKept(mirror.choice, vaultId, `${folder}/x`)) return [];
    // A folder kept whole takes the folders kept below it into itself.
    const keep = () =>
      void chooseOffline(mirror, (c) => ({
        ...c,
        kept: { ...c.kept, [vaultId]: [...(c.kept[vaultId] ?? []).filter((f) => folder !== "" && !f.startsWith(`${folder}/`)), folder] },
      })).then(changed);
    return [{ label: "Keep offline", icon: CloudDownload, onSelect: keep }];
  };
}
