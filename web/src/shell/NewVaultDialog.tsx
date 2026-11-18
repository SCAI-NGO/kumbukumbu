// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { byokProvider, useByok } from "../api/notes";
import { useDialog } from "../hooks/useDialog";
import { FormError, primaryButtonClass, secondaryButtonClass, TextField } from "../components/form";

/**
 * Asks for a vault's name and creates it; the server says what a name may be. With her own TensorX key saved, the server
 * starts the vault on TensorX (BYOK spec): the dialog says so in one line, and unticked, the vault stays on this server.
 */
export default function NewVaultDialog({ onClose, onCreated }: { onClose(): void; onCreated(name: string): void }) {
  const [name, setName] = useState("");
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const byok = useByok();
  const withKey = byok.data?.offered === true && byok.data.keySaved;
  const [tensorx, setTensorx] = useState(true);
  const create = useMutation({
    mutationFn: async () => {
      const vault = await call(api.POST("/api/vaults", { body: { name } }));
      if (vault.embeddings === byokProvider && !tensorx) {
        await call(api.PUT("/api/vaults/{vault}/embeddings", { params: { path: { vault: vault.name } }, body: { provider: "internal" } }));
      }
      return vault;
    },
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
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <form
        ref={dialog}
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
        {withKey && (
          <div className="flex min-h-11 items-start gap-3 text-text">
            <input
              id="new-vault-tensorx"
              type="checkbox"
              checked={tensorx}
              onChange={(e) => setTensorx(e.target.checked)}
              aria-describedby="new-vault-tensorx-note"
              className="mt-1 size-4 flex-none accent-accent"
            />
            <span className="flex flex-col gap-0.5">
              <label htmlFor="new-vault-tensorx">Search by meaning with TensorX (your own key)</label>
              <span id="new-vault-tensorx-note" className="text-sm text-muted">
                Its text and the searches that include it go to TensorX. Unticked, it stays on this server.
              </span>
            </span>
          </div>
        )}
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
