// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { EmbeddingProvider } from "../api/notes";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/** A text from the server or the admin as a sentence: one full stop at the end, whether it brought one or not. */
const sentence = (text: string) => text.replace(/[.\s]+$/, "") + ".";

/**
 * Where a vault's search by meaning runs: on this server, or at one of the external providers the admin set up. Before
 * an external one is chosen, the dialog says in plain words what that sends, to whom, and that their promise is theirs.
 */
export default function EmbeddingsDialog({
  vault,
  current,
  providers,
  onClose,
}: {
  vault: string;
  current: string;
  providers: EmbeddingProvider[];
  onClose(): void;
}) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const queryClient = useQueryClient();
  const [chosen, setChosen] = useState(current);
  const picked = providers.find((p) => p.name === chosen);
  const before = providers.find((p) => p.name === current);
  const save = useMutation({
    mutationFn: () => call(api.PUT("/api/vaults/{vault}/embeddings", { params: { path: { vault } }, body: { provider: chosen } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["vaults"] });
      onClose();
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (chosen !== current) save.mutate();
  };

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={submit}
        className="k-sheet flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <div>
          <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
            Search by meaning in {vault}
          </h2>
          <p className="mt-1 text-text-body">
            To find notes by what they mean, each section is turned into numbers by a language model. Choose where that model runs.
          </p>
        </div>

        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">Where the model runs</legend>
          {providers.map((p) => (
            <label
              key={p.name}
              className={`flex min-h-11 cursor-pointer items-start gap-3 rounded-[12px] border p-3 ${p.name === chosen ? "border-accent bg-accent/10" : "border-line bg-surface-2"}`}
            >
              <input
                type="radio"
                name={`${id}-provider`}
                value={p.name}
                checked={p.name === chosen}
                onChange={() => setChosen(p.name)}
                className="mt-1 size-4 flex-none accent-accent"
              />
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="font-medium text-text">
                  {p.label}
                  {p.name === current && <span className="text-muted"> (now)</span>}
                </span>
                <span className="text-sm text-muted">{p.external ? p.description || "An external service." : "Nothing leaves this server."}</span>
                {!p.available && p.reason && <span className="text-sm text-muted">Not available right now: {sentence(p.reason)}</span>}
              </span>
            </label>
          ))}
        </fieldset>

        {picked?.external && chosen !== current && (
          <div id={`${id}-warning`} role="note" className="flex flex-col gap-2 rounded-[12px] border border-line bg-surface-2 p-3 text-text-body">
            <p>
              <strong className="text-text">The text of every section in {vault} is sent to {picked.label}</strong>, now for the notes already there and
              from then on for every change.
            </p>
            <p>
              <strong className="text-text">So is the text of every search that includes {vault}</strong>: also a search across all your vaults, whether you
              make it or an agent with a key for {vault} does.
            </p>
            <p>It is sent encrypted on the way, but {picked.label} reads it in order to answer.</p>
            {picked.description && <p>What {picked.label} promises: {sentence(picked.description)}</p>}
            <p>
              That promise is theirs: Kumbu² can't check or enforce what happens to your text there. The notes of your other vaults are not sent; only
              the searches that include {vault} are.
            </p>
          </div>
        )}
        {!picked?.external && chosen !== current && before?.external && (
          <p id={`${id}-warning`} role="note" className="rounded-[12px] border border-line bg-surface-2 p-3 text-text-body">
            The notes are embedded on this server again, and nothing more is sent to {before.label}. What was sent before stays under their terms.
          </p>
        )}

        {save.isError && <FormError>{errorText(save.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button
            type="submit"
            disabled={save.isPending || chosen === current}
            aria-describedby={chosen !== current && (picked?.external || before?.external) ? `${id}-warning` : undefined}
            className={primaryButtonClass}
          >
            {picked?.external && chosen !== current ? `Send to ${picked.label}` : "Use this server"}
          </button>
        </div>
      </form>
    </div>
  );
}
