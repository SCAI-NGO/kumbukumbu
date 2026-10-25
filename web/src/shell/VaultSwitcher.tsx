// SPDX-License-Identifier: AGPL-3.0-or-later
import { Plus } from "lucide-react";
import { useVaults } from "../api/notes";
import { useEditor } from "../state/editor";

const chip = "min-h-11 flex-none rounded-full border px-3.5 text-sm whitespace-nowrap focus-visible:outline-2 focus-visible:outline-accent";

/** The vaults as chips at the bottom of the drawer, and the way to a new one. */
export default function VaultSwitcher({ onNewVault }: { onNewVault(): void }) {
  const vaults = useVaults();
  const active = useEditor((s) => s.activeVault);
  const setActiveVault = useEditor((s) => s.setActiveVault);
  return (
    <div role="group" aria-label="Switch vault" className="flex gap-1.5 overflow-x-auto [scrollbar-width:none]">
      {vaults.data?.map((v) => (
        <button
          key={v.name}
          type="button"
          aria-pressed={v.name === active}
          onClick={() => setActiveVault(v.name)}
          className={`${chip} ${v.name === active ? "border-accent bg-accent text-on-accent" : "border-line bg-surface text-text-body"}`}
        >
          {v.name}
        </button>
      ))}
      <button type="button" onClick={onNewVault} className={`${chip} flex items-center gap-1 border-dashed border-line text-muted`}>
        <Plus size={16} aria-hidden />
        New vault
      </button>
    </div>
  );
}
