// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { vaultOf } from "../api/paths";
import { useRenameVault, useVaultAccess } from "../api/sharing";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";

/** "Rename <vault>", the owner's: what breaks, before the name is changed; afterwards the open note under the new name. */
export default function RenameVaultDialog({ vault, onClose }: { vault: string; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const access = useVaultAccess(vault);
  const rename = useRenameVault();
  const [name, setName] = useState(vault);
  const [problem, setProblem] = useState<string | null>(null);
  const to = name.normalize("NFC").trim();
  const changed = to !== vault && to !== "";
  // A device that breaks isn't left unsaid because the list didn't load.
  const devices = access.isError || !!access.data?.deviceKeys.length;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!changed || access.isPending) return;
    setProblem(null);
    // The open note's pending text is saved first: once renamed, a save to the old path would find no vault.
    const { currentPath, beforeRead: save } = useEditor.getState();
    if (currentPath && vaultOf(currentPath) === vault && save && !(await save())) return setProblem("The open note has changes that can't be saved yet. Resolve them first.");
    rename.mutate({ vault, name: to }, { onSuccess: onClose });
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
        <form onSubmit={(e) => void submit(e)} className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-name`} className={labelClass}>
              New name
            </label>
            <input
              id={`${id}-name`}
              aria-describedby={`${id}-lead`}
              value={name}
              maxLength={64}
              onChange={(e) => {
                setName(e.target.value);
                setProblem(null);
                rename.reset();
              }}
              autoComplete="off"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              className={inputClass}
            />
            {problem && <FormError>{problem}</FormError>}
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
