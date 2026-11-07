// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import type { components } from "../api/schema";
import { FormError, primaryButtonClass, secondaryButtonClass, TextField } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { ExpiryPicker, grant, RightsPicker, type Right } from "./NewKeyDialog";

type Created = components["schemas"]["CreatedDavShareView"];

/**
 * Asks for a WebDAV share's label, its right per vault and its lifetime, as for an API key but never with May publish
 * (WebDAV design §3.2). The server asks for the password when its window has closed; the app's prompt takes it.
 */
export default function NewShareDialog({ onClose, onCreated }: { onClose(): void; onCreated(created: Created): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const vaults = useVaults();
  const [label, setLabel] = useState("");
  const [chosen, setChosen] = useState<Record<string, Right>>({});
  // As for a key: a share that expires by itself stops working on its own when the machine is forgotten.
  const [expiresInDays, setExpiresInDays] = useState(90);

  const granted = grant(chosen);
  const ready = label.trim() !== "" && Object.keys(granted).length > 0;

  const create = useMutation({
    mutationFn: () => call(api.POST("/api/dav-shares", { body: { label, vaults: granted, ...(expiresInDays ? { expiresInDays } : {}) } })),
    // The result carries the password: it goes to the connect page's state, not the client-wide mutation cache.
    gcTime: 0,
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["dav-shares"] });
      onCreated(created);
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (ready) create.mutate();
  };

  return (
    <div className="fixed inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={submit}
        className="k-sheet flex max-h-dvh w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
          New WebDAV share
        </h2>
        <TextField label="Label" value={label} onChange={setLabel} autoComplete="off" />

        <RightsPicker vaults={vaults} chosen={chosen} onChange={setChosen}>
          A share needs a label, such as the machine it is for, and at least one vault. Each vault is a folder on the drive; Read and write
          is enough to edit notes, Maintain only for a machine you trust with the vault's structure.
        </RightsPicker>

        <ExpiryPicker value={expiresInDays} onChange={setExpiresInDays}>
          From then on the share opens nothing, as if revoked; make a new one for the drive.
        </ExpiryPicker>

        {create.isError && <FormError>{errorText(create.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={create.isPending || !ready} className={primaryButtonClass}>
            Create share
          </button>
        </div>
      </form>
    </div>
  );
}
