// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { FormError, primaryButtonClass, secondaryButtonClass, TextField } from "../components/form";

/** Asks for a vault's name and creates it; the server says what a name may be. */
export default function NewVaultDialog({ onClose, onCreated }: { onClose(): void; onCreated(name: string): void }) {
  const [name, setName] = useState("");
  const queryClient = useQueryClient();
  const create = useMutation({
    mutationFn: () => call(api.POST("/api/vaults", { body: { name } })),
    onSuccess: async (vault) => {
      await queryClient.invalidateQueries({ queryKey: ["vaults"] });
      onCreated(vault.name);
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    create.mutate();
  };

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center" onKeyDown={(e) => e.key === "Escape" && onClose()}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-vault-title"
        onSubmit={submit}
        className="k-sheet flex w-full max-w-md flex-col gap-4 rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id="new-vault-title" className="text-[22px] font-bold text-text">
          New vault
        </h2>
        <TextField label="Name" value={name} onChange={setName} autoComplete="off" />
        {create.isError && <FormError>{errorText(create.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={create.isPending || !name.trim()} className={primaryButtonClass}>
            Create vault
          </button>
        </div>
      </form>
    </div>
  );
}
