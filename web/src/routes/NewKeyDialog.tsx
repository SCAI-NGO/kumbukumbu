// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient, type UseQueryResult } from "@tanstack/react-query";
import { useId, useState, type FormEvent, type ReactNode } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import type { components } from "../api/schema";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass, TextField } from "../components/form";
import { useDialog } from "../hooks/useDialog";

type Created = components["schemas"]["CreatedApiKeyView"];
export type Right = "none" | "read" | "write";

const rights: { value: Right; label: string }[] = [
  { value: "none", label: "None" },
  { value: "read", label: "Read" },
  { value: "write", label: "Read and write" },
];

/** The lifetimes the server accepts; 0 stands for "never". */
export const expiries: { days: number; label: string }[] = [
  { days: 30, label: "30 days" },
  { days: 90, label: "90 days" },
  { days: 365, label: "1 year" },
  { days: 0, label: "Never" },
];

const segmentClass =
  "flex min-h-11 flex-1 cursor-pointer items-center justify-center rounded-[8px] px-2 text-center text-sm text-text-body has-checked:bg-accent has-checked:text-on-accent has-focus-visible:outline-2 has-focus-visible:outline-accent";

/** The vaults that got a right, as the server takes them. */
export const grant = (chosen: Record<string, Right>) => Object.fromEntries(Object.entries(chosen).filter(([, right]) => right !== "none"));

/** A right per vault, as one group of radios for each; "none" leaves the vault out. `children` is the hint underneath. */
export function RightsPicker({
  vaults,
  chosen,
  onChange,
  children,
}: {
  vaults: UseQueryResult<{ name: string }[]>;
  chosen: Record<string, Right>;
  onChange(chosen: Record<string, Right>): void;
  children?: ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex flex-col gap-3">
      <p className={labelClass}>Vaults</p>
      {vaults.isPending && <p className="text-muted">Loading your vaults…</p>}
      {vaults.isError && <FormError>{errorText(vaults.error)}</FormError>}
      {vaults.data?.length === 0 && <p className="text-text-body">You have no vault yet. Make one first.</p>}
      {vaults.data?.map((vault) => (
        <fieldset key={vault.name} className="flex min-w-0 flex-col gap-1">
          <legend className="mb-1 text-text">{vault.name}</legend>
          <div className="flex rounded-[10px] border border-line bg-surface-2 p-0.5">
            {rights.map((right) => (
              <label key={right.value} className={segmentClass}>
                <input
                  type="radio"
                  name={`${id}-${vault.name}`}
                  value={right.value}
                  checked={(chosen[vault.name] ?? "none") === right.value}
                  onChange={() => onChange({ ...chosen, [vault.name]: right.value })}
                  className="sr-only"
                />
                {right.label}
              </label>
            ))}
          </div>
        </fieldset>
      ))}
      {children && <p className="text-sm text-muted">{children}</p>}
    </div>
  );
}

/** How long it lasts; 0 stands for "never". `children` is the hint underneath. */
export function ExpiryPicker({ value, onChange, children }: { value: number; onChange(days: number): void; children?: ReactNode }) {
  const id = useId();
  return (
    <fieldset className="flex min-w-0 flex-col gap-1">
      <legend className={`${labelClass} mb-1`}>Expires after</legend>
      <div className="flex rounded-[10px] border border-line bg-surface-2 p-0.5">
        {expiries.map((expiry) => (
          <label key={expiry.days} className={segmentClass}>
            <input
              type="radio"
              name={`${id}-expiry`}
              value={expiry.days}
              checked={value === expiry.days}
              onChange={() => onChange(expiry.days)}
              className="sr-only"
            />
            {expiry.label}
          </label>
        ))}
      </div>
      {children && <p className="text-sm text-muted">{children}</p>}
    </fieldset>
  );
}

/** Asks for a key's name and its right per vault; the server says what a name may be. */
export default function NewKeyDialog({ onClose, onCreated }: { onClose(): void; onCreated(created: Created): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const vaults = useVaults();
  const [name, setName] = useState("");
  const [chosen, setChosen] = useState<Record<string, Right>>({});
  // A key that expires by itself is the safer default: a forgotten one stops working on its own.
  const [expiresInDays, setExpiresInDays] = useState(90);

  const granted = grant(chosen);
  const ready = name.trim() !== "" && Object.keys(granted).length > 0;

  const create = useMutation({
    mutationFn: () => call(api.POST("/api/keys", { body: { name, vaults: granted, ...(expiresInDays ? { expiresInDays } : {}) } })),
    // The result carries the key: it goes to the reveal's state, not the client-wide mutation cache.
    gcTime: 0,
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["keys"] });
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
          New API key
        </h2>
        <TextField label="Name" value={name} onChange={setName} autoComplete="off" />

        <RightsPicker vaults={vaults} chosen={chosen} onChange={setChosen}>
          A key needs a name and at least one vault it may read.
        </RightsPicker>

        <ExpiryPicker value={expiresInDays} onChange={setExpiresInDays}>
          From then on the key opens nothing, as if revoked; make a new one for the agent.
        </ExpiryPicker>

        {create.isError && <FormError>{errorText(create.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={create.isPending || !ready} className={primaryButtonClass}>
            Create key
          </button>
        </div>
      </form>
    </div>
  );
}
