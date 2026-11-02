// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { noteUrl, vaultOf } from "../api/paths";
import { useRenameVault, useVaultAccess } from "../api/sharing";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { movedPath, useEditor } from "../state/editor";

/**
 * "Rename <vault>", the owner's: what breaks before the name is changed, and afterwards the open note under the new
 * name. Opened only from the drawer, which shows only on `/` and on a note, so an open note is all there is to follow.
 */
export default function RenameVaultDialog({ vault, onClose }: { vault: string; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const navigate = useNavigate();
  const access = useVaultAccess(vault);
  const rename = useRenameVault();
  const [name, setName] = useState(vault);
  const to = name.normalize("NFC").trim();
  const changed = to !== vault && to !== "";
  // A device that breaks isn't left unsaid because the list didn't load.
  const devices = access.isError || !!access.data?.deviceKeys.length;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!changed || access.isPending) return;
    const open = useEditor.getState().currentPath;
    rename.mutate(
      { vault, name: to },
      {
        onSuccess: () => {
          if (open && vaultOf(open) === vault) navigate(noteUrl(movedPath(open, `/${vault}/`, `/${to}/`)), { replace: true });
          onClose();
        },
      },
    );
  };

  return (
    <div className="fixed inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-dvh w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
          Rename {vault}
        </h2>
        <p id={`${id}-lead`} className="text-text-body">
          Members keep their own name for it. Agents find it under the new name; paths with the old one stop working.
          {devices && " Connected Obsidian devices need to pair again."}
        </p>
        <form onSubmit={submit} className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-name`} className={labelClass}>
              New name
            </label>
            <input
              id={`${id}-name`}
              aria-describedby={`${id}-lead`}
              value={name}
              onChange={(e) => {
                setName(e.target.value);
                rename.reset();
              }}
              autoComplete="off"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              className={inputClass}
            />
            {rename.isError && <FormError>{errorText(rename.error)}</FormError>}
          </div>
          <div className="flex gap-3">
            <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
              Cancel
            </button>
            <button type="submit" disabled={!changed || access.isPending || rename.isPending} className={`${primaryButtonClass} flex-1`}>
              Rename vault
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
