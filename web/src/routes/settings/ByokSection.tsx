// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import { byokProvider, useByok, useVaults } from "../../api/notes";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { useDialog } from "../../hooks/useDialog";
import { Section } from "./Section";

const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });

/**
 * Her own TensorX key (BYOK spec): shown only where the server offers it. Saved once for the account, then chosen vault by
 * vault; after the first save the app asks once which of her vaults go along, all ticked, and moves none silently,
 * because from then on their text leaves the server.
 */
export default function ByokSection() {
  const id = useId();
  const byok = useByok();
  const vaults = useVaults();
  const queryClient = useQueryClient();
  const [key, setKey] = useState("");
  const [replacing, setReplacing] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [offer, setOffer] = useState<string[] | null>(null);

  const save = useMutation({
    mutationFn: () => call(api.PUT("/api/account/embeddings-key", { body: { key: key.trim() } })),
    onSuccess: async (view) => {
      const first = !byok.data?.keySaved;
      setKey("");
      setReplacing(false);
      queryClient.setQueryData(["byok"], view);
      await queryClient.invalidateQueries({ queryKey: ["embeddings"] });
      const movable = (vaults.data ?? []).filter((v) => v.level === "owner" && !v.discreet && !v.pending && v.embeddings !== byokProvider).map((v) => v.name);
      if (first && movable.length > 0) setOffer(movable);
    },
  });
  const remove = useMutation({
    mutationFn: () => call(api.DELETE("/api/account/embeddings-key")),
    onSuccess: async () => {
      setRemoving(false);
      await Promise.all(["byok", "embeddings", "vaults"].map((k) => queryClient.invalidateQueries({ queryKey: [k] })));
    },
  });

  if (!byok.data?.offered) return null;
  const view = byok.data;
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (key.trim()) save.mutate();
  };

  return (
    <Section title="Search by meaning">
      <p className="text-text-body">
        This server finds notes by meaning with its own model. With a TensorX key of your own, the vaults you choose are found with TensorX's model, which
        ranks better. Their text, and the searches that include them, then go to TensorX under your own contract with them.
      </p>
      {view.keySaved && !replacing ? (
        <>
          <p className="text-text">TensorX key: saved, ends in …{view.last4}</p>
          {view.refusedAt && <p className="text-text-body">TensorX refused the key on {when(view.refusedAt)}; its vaults are found by words until you replace it.</p>}
          <p className="text-sm text-muted">Choose which vaults use it in each vault's ⋯ menu, "Search by meaning…", or when you make a new vault.</p>
          {removing ? (
            <div role="group" aria-label="Remove the key" className="flex flex-col gap-2 rounded-[12px] border border-line bg-surface-2 p-3">
              <p className="text-text-body">The vaults that use it go back to this server and are embedded here again; nothing more is sent to TensorX.</p>
              {remove.isError && <FormError>{errorText(remove.error)}</FormError>}
              <div className="flex gap-2">
                <button type="button" onClick={() => setRemoving(false)} className={secondaryButtonClass}>
                  Cancel
                </button>
                <button type="button" disabled={remove.isPending} onClick={() => remove.mutate()} className={secondaryButtonClass}>
                  Remove
                </button>
              </div>
            </div>
          ) : (
            <div className="flex gap-2">
              <button type="button" onClick={() => setReplacing(true)} className={secondaryButtonClass}>
                Replace key
              </button>
              <button type="button" onClick={() => setRemoving(true)} className={secondaryButtonClass}>
                Remove key
              </button>
            </div>
          )}
        </>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-3">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-key`} className={labelClass}>
              TensorX key
            </label>
            <input
              id={`${id}-key`}
              type="password"
              value={key}
              onChange={(e) => setKey(e.target.value)}
              autoComplete="off"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              data-1p-ignore="true"
              data-lpignore="true"
              data-bwignore="true"
              className={`${inputClass} font-mono`}
            />
          </div>
          <p className="text-sm text-muted">It is checked with TensorX once, then kept encrypted; it is never shown again.</p>
          {save.isError && <FormError>{errorText(save.error)}</FormError>}
          <div className="flex gap-2">
            {replacing && (
              <button type="button" onClick={() => setReplacing(false)} className={secondaryButtonClass}>
                Cancel
              </button>
            )}
            <button type="submit" disabled={save.isPending || !key.trim()} className={primaryButtonClass}>
              Save key
            </button>
          </div>
        </form>
      )}
      {offer && <MoveDialog vaults={offer} onClose={() => setOffer(null)} />}
    </Section>
  );
}

/** "Move these N vaults to TensorX too?": each of her own vaults, all ticked; Not now moves none. */
function MoveDialog({ vaults, onClose }: { vaults: string[]; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const queryClient = useQueryClient();
  const [chosen, setChosen] = useState(() => new Set(vaults));
  const move = useMutation({
    mutationFn: async () => {
      for (const vault of vaults.filter((v) => chosen.has(v))) {
        await call(api.PUT("/api/vaults/{vault}/embeddings", { params: { path: { vault } }, body: { provider: byokProvider } }));
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["vaults"] }),
    onSuccess: onClose,
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
          move.mutate();
        }}
        className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Move {vaults.length === 1 ? "this vault" : `these ${vaults.length} vaults`} to TensorX too?
        </h2>
        <p className="text-text-body">
          The text of every section in the vaults you tick is sent to TensorX, now and for every change, and so is every search that includes them.
          They are embedded again in the background and found by words until then.
        </p>
        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">Vaults to move</legend>
          {vaults.map((vault) => (
            <label key={vault} className="flex min-h-11 items-center gap-3 text-text">
              <input
                type="checkbox"
                name={vault}
                checked={chosen.has(vault)}
                onChange={(e) =>
                  setChosen((before) => {
                    const next = new Set(before);
                    if (e.target.checked) next.add(vault);
                    else next.delete(vault);
                    return next;
                  })
                }
                className="size-4 flex-none accent-accent"
              />
              <span className="min-w-0 break-words">{vault}</span>
            </label>
          ))}
        </fieldset>
        {move.isError && <FormError>{errorText(move.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Not now
          </button>
          <button type="submit" disabled={move.isPending || chosen.size === 0} className={`${primaryButtonClass} flex-1`}>
            Move
          </button>
        </div>
      </form>
    </div>
  );
}
