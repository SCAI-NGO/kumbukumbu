// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronsUpDown, Ellipsis, LayoutList, PenLine, Plus } from "lucide-react";
import { useId, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { groupVaults, useVaults, type VaultView } from "../api/notes";
import { useDialog } from "../hooks/useDialog";
import { useTruncated } from "../hooks/useTruncated";
import { useEditor } from "../state/editor";

const entryClass = "flex min-h-11 w-full min-w-0 items-center gap-2.5 rounded-[8px] px-3 text-left text-[15px] focus-visible:outline-2 focus-visible:outline-accent";
const groupTitle = "truncate px-3 pt-2 pb-1 font-mono text-[11px] tracking-[0.08em] text-dim uppercase";

function VaultEntry({ vault, current, onChoose }: { vault: VaultView; current: boolean; onChoose(): void }) {
  const { ref, ...truncated } = useTruncated<HTMLSpanElement>(vault.name);
  return (
    <li>
      <button
        type="button"
        aria-current={current ? "true" : undefined}
        data-autofocus={current ? true : undefined}
        onClick={onChoose}
        {...truncated}
        className={`${entryClass} ${current ? "bg-accent/15 font-medium text-accent-text" : "text-text hover:bg-surface"}`}
      >
        <span ref={ref} className="min-w-0 flex-1 truncate">
          {vault.name}
        </span>
      </button>
    </li>
  );
}

function Action({ icon, onSelect, children }: { icon: ReactNode; onSelect(): void; children: ReactNode }) {
  return (
    <button type="button" onClick={onSelect} className={`${entryClass} text-text-body hover:bg-surface`}>
      {icon}
      {children}
    </button>
  );
}

/**
 * The vaults' popout: the user's own first, then those shared with her, each owner's together under the owner's name;
 * then a new vault, the overview of all of them, and More vaults… last, always (discreet spec §3.6): whether or not
 * she has a PIN or discreet vaults, so its presence tells nothing. A sheet from the bottom on a phone; from `md` on, a
 * panel above the switcher, wider than a narrow sidebar so that names fit. A modal dialog either way: Escape or a tap
 * beside it closes it, and the focus goes back to the switcher.
 */
function Popout({ onClose, onNewVault, onRename, onMoreVaults }: { onClose(): void; onNewVault(): void; onRename?(): void; onMoreVaults(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const navigate = useNavigate();
  const vaults = useVaults();
  const active = useEditor((s) => s.activeVault);
  const { own, shared } = groupVaults(vaults.data ?? []);
  const current = vaults.data?.find((v) => v.name === active);
  const then = (action: () => void) => () => {
    onClose();
    action();
  };
  const entry = (v: VaultView) => <VaultEntry key={v.name} vault={v} current={v.name === active} onChoose={then(() => useEditor.getState().setActiveVault(v.name))} />;
  return (
    <>
      <div data-testid="vault-popout-scrim" aria-hidden onClick={onClose} className="fixed inset-0 z-30 bg-black/55 md:bg-transparent" />
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet fixed inset-x-0 bottom-0 z-40 flex max-h-[85dvh] flex-col overflow-y-auto rounded-t-[16px] border border-line bg-surface p-2 pb-[max(16px,env(safe-area-inset-bottom))] md:absolute md:inset-x-auto md:bottom-full md:left-0 md:mb-2 md:max-h-[min(70dvh,32rem)] md:w-80 md:max-w-[calc(100vw-2rem)] md:rounded-[12px] md:bg-surface-2 md:pb-2 md:shadow-lg"
      >
        <h2 id={`${id}-title`} className="px-3 pt-2 pb-1 text-[20px] font-bold text-text md:sr-only">
          Vaults
        </h2>
        <h3 id={`${id}-own`} className={groupTitle}>
          Your vaults
        </h3>
        <ul aria-labelledby={`${id}-own`}>{own.map(entry)}</ul>
        {shared.length > 0 && (
          <>
            <h3 className={`${groupTitle} mt-2 border-t border-line pt-3`}>Shared with me</h3>
            {shared.map(({ key, owner, vaults: list }) => (
              <section key={key}>
                {/* The owner's display name, as she typed it: quoted by the server, so it reads as a name and nothing more. */}
                <p aria-hidden className="truncate px-3 pt-1 text-[12px] text-muted">
                  {owner}
                </p>
                <ul aria-label={`Shared by ${owner}`}>{list.map(entry)}</ul>
              </section>
            ))}
          </>
        )}
        <div className="mt-2 flex flex-col border-t border-line pt-2">
          {current?.membership && onRename && (
            <Action icon={<PenLine size={18} strokeWidth={1.8} aria-hidden className="flex-none text-muted" />} onSelect={then(onRename)}>
              Your name for this vault…
            </Action>
          )}
          <Action icon={<Plus size={18} strokeWidth={1.8} aria-hidden className="flex-none text-muted" />} onSelect={then(onNewVault)}>
            New vault
          </Action>
          <Action
            icon={<LayoutList size={18} strokeWidth={1.8} aria-hidden className="flex-none text-muted" />}
            onSelect={then(() => {
              // The overview is a page of its own: the drawer, left open, would be open again on the way back.
              useEditor.getState().setOverlay("none");
              navigate("/", { state: { overview: true } });
            })}
          >
            All vaults
          </Action>
          <Action icon={<Ellipsis size={18} strokeWidth={1.8} aria-hidden className="flex-none text-muted" />} onSelect={then(onMoreVaults)}>
            More vaults…
          </Action>
        </div>
      </div>
    </>
  );
}

/**
 * The switcher at the bottom of the drawer: a button with the current vault's name, which opens the popout of all of
 * them. `onRename` opens her side of a vault shared with her, where she gives it her own name; `onMoreVaults` the PIN
 * dialog.
 */
export default function VaultSwitcher({ onNewVault, onRename, onMoreVaults }: { onNewVault(): void; onRename?(): void; onMoreVaults(): void }) {
  const active = useEditor((s) => s.activeVault);
  const [open, setOpen] = useState(false);
  const { ref, ...truncated } = useTruncated<HTMLSpanElement>(active ?? "");
  return (
    <div className="relative min-w-0">
      <button
        type="button"
        aria-label={active ? `Switch vault: ${active}` : "Switch vault"}
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        {...truncated}
        className="flex min-h-11 w-full min-w-0 items-center gap-2 rounded-[10px] border border-line bg-surface px-3.5 text-left text-[15px] text-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        <span ref={ref} className="min-w-0 flex-1 truncate font-medium">
          {active ?? "Vaults"}
        </span>
        <ChevronsUpDown size={16} aria-hidden className="flex-none text-muted" />
      </button>
      {open && <Popout onClose={() => setOpen(false)} onNewVault={onNewVault} onRename={onRename} onMoreVaults={onMoreVaults} />}
    </div>
  );
}
