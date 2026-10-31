// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import type { components } from "../api/schema";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { ExpiryPicker, grant, RightsPicker, type Right } from "./NewKeyDialog";

type Connection = components["schemas"]["ConnectionView"];

/** Asks for a connection's name, its right per vault and its lifetime; the server says what a name may be. */
export default function NewConnectionDialog({ onClose, onCreated }: { onClose(): void; onCreated(view: Connection): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const vaults = useVaults();
  const [name, setName] = useState("");
  const [chosen, setChosen] = useState<Record<string, Right>>({});
  // As for keys: one that ends by itself is the safer default.
  const [expiresInDays, setExpiresInDays] = useState(90);

  const granted = grant(chosen);
  const ready = name.trim() !== "" && Object.keys(granted).length > 0;

  const create = useMutation({
    mutationFn: () => call(api.POST("/api/connections", { body: { name, vaults: granted, ...(expiresInDays ? { expiresInDays } : {}) } })),
    // The address is no secret, but it has no business in the client-wide cache either.
    gcTime: 0,
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["connections"] });
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
          Connect an app
        </h2>

        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-name`} className={labelClass}>
            Name
          </label>
          <input
            id={`${id}-name`}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Work"
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            required
            className={inputClass}
          />
          {/* Only the instructions after creating depend on the app; nothing is filled in here. */}
          <p className="flex flex-wrap items-center gap-2 text-sm text-muted">
            Instructions for
            <span className="inline-flex min-h-8 items-center rounded-full border border-accent px-3 text-text">Claude</span>
          </p>
        </div>

        <RightsPicker vaults={vaults} chosen={chosen} onChange={setChosen}>
          A connection needs a name and at least one vault it may read. Read and write is enough for most apps; choose Maintain only for one you
          trust with the vault's structure.
        </RightsPicker>

        <ExpiryPicker value={expiresInDays} onChange={setExpiresInDays}>
          From then on the connection opens nothing, as if revoked; make a new one for the app.
        </ExpiryPicker>

        {create.isError && <FormError>{errorText(create.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={create.isPending || !ready} className={primaryButtonClass}>
            Connect an app
          </button>
        </div>
      </form>
    </div>
  );
}
