// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { download, exportUrl } from "./exportUrl";

const notes = (n: number) => `${n} ${n === 1 ? "note" : "notes"}`;

/** Every vault of the user, all ticked; any can be unticked. A vault being re-encrypted can't be exported until it is done. */
export default function ExportDialog({ onClose }: { onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const vaults = useVaults();
  // The unticked ones, so a vault that appears while the dialog is open starts ticked like the rest.
  const [unticked, setUnticked] = useState<ReadonlySet<string>>(new Set());
  const chosen = (vaults.data ?? []).filter((v) => !v.pending && !unticked.has(v.name)).map((v) => v.name);

  const toggle = (name: string) =>
    setUnticked((current) => {
      const next = new Set(current);
      if (!next.delete(name)) next.add(name);
      return next;
    });

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={(e) => {
          e.preventDefault();
          if (chosen.length === 0) return;
          download(exportUrl(chosen));
          onClose();
        }}
        className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold text-text">
          Export my vaults
        </h2>
        <p className="text-text-body">One ZIP with a folder per vault you tick.</p>
        {vaults.isPending && <p className="text-muted">Loading your vaults…</p>}
        {vaults.isError && <FormError>{errorText(vaults.error)}</FormError>}
        {vaults.data?.length === 0 && <p className="text-muted">You have no vault yet.</p>}
        {vaults.data && vaults.data.length > 0 && (
          <ul className="flex flex-col gap-2">
            {vaults.data.map((vault) => (
              <li key={vault.name}>
                <label className="flex items-center gap-3">
                  <input
                    type="checkbox"
                    checked={!vault.pending && !unticked.has(vault.name)}
                    disabled={vault.pending}
                    onChange={() => toggle(vault.name)}
                    className="size-4 shrink-0 accent-accent"
                  />
                  <span className="min-w-0 flex-1 break-words text-text">{vault.name}</span>
                  <span className="shrink-0 text-sm text-muted">{vault.pending ? "Being re-encrypted" : notes(Number(vault.notes))}</span>
                </label>
              </li>
            ))}
          </ul>
        )}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={chosen.length === 0} className={`${primaryButtonClass} flex-1`}>
            Export
          </button>
        </div>
      </form>
    </div>
  );
}
