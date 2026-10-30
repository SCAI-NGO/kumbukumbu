// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { findFolder, useTree } from "../api/notes";
import { markerOf, vaultOf } from "../api/paths";
import { FormError, primaryButtonClass, secondaryButtonClass, TextField } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { putFile } from "../upload/putFile";

/**
 * Asks for a folder's name and makes it in `parent` (`/Work/Drafts`): an empty folder is kept by its marker, so that
 * is what is written. One that is there already is shown instead; the server says what a name may be.
 */
export default function NewFolderDialog({ parent, onClose }: { parent: string; onClose(): void }) {
  const [name, setName] = useState("");
  const [exists, setExists] = useState<string | null>(null);
  const vault = vaultOf(parent);
  const tree = useTree(vault);
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const create = useMutation({
    mutationFn: (path: string) => putFile(vault, markerOf(path), new Uint8Array(0)),
    // `created`, or `conflict`: made meanwhile, so the folder is there all the same.
    onSuccess: async (_, path) => {
      await queryClient.invalidateQueries({ queryKey: ["tree", vault] });
      useEditor.getState().revealFolder(path);
      onClose();
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const path = `${parent}/${name.trim()}`;
    if (tree.data && findFolder(tree.data.root, path)) {
      setExists(name.trim());
      create.reset();
      useEditor.getState().revealFolder(path);
      return;
    }
    setExists(null);
    create.mutate(path);
  };

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-folder-title"
        onSubmit={submit}
        className="k-sheet flex w-full max-w-md flex-col gap-4 rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id="new-folder-title" className="text-[22px] font-bold text-text">
          New folder
        </h2>
        <TextField label="Name" value={name} onChange={setName} autoComplete="off" />
        {exists !== null && <FormError>A folder named "{exists}" exists already.</FormError>}
        {create.isError && <FormError>{errorText(create.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={create.isPending || !name.trim()} className={primaryButtonClass}>
            Create folder
          </button>
        </div>
      </form>
    </div>
  );
}
