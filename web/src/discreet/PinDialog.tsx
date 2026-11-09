// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useRef, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { segmentClass } from "../routes/NewKeyDialog";
import { useReveal } from "./api";
import { minutesOf, readTimeout, timeouts, writeTimeout, type DiscreetTimeout } from "./timeout";

const timeoutLabel = (t: DiscreetTimeout) => (t === "never" ? "Never" : `${t} min`);

/**
 * "More vaults": the PIN, and how long the discreet vaults stay shown, preselected with this device's last choice. It
 * looks and answers the same whether or not she has a PIN or discreet vaults (spec §3.6): a wrong PIN and none set are
 * one sentence, and the field is cleared for the next try.
 */
export default function PinDialog({ onClose }: { onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const [pin, setPin] = useState("");
  const field = useRef<HTMLInputElement>(null);
  const [choice, setChoice] = useState(readTimeout);
  const reveal = useReveal(pin);
  const complete = /^[0-9]{4,6}$/.test(pin);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!complete || reveal.isPending) return;
    writeTimeout(choice);
    reveal.mutate(minutesOf(choice), {
      onSuccess: onClose,
      onError: () => {
        setPin("");
        field.current?.focus();
      },
    });
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
        <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
          More vaults
        </h2>
        <form onSubmit={submit} className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${id}-pin`} className={labelClass}>
              PIN
            </label>
            {/* A password field so that the digits aren't shown; the data-* attributes tell password managers to leave it alone, as it is no password. */}
            <input
              ref={field}
              id={`${id}-pin`}
              type="password"
              inputMode="numeric"
              autoComplete="off"
              maxLength={6}
              aria-describedby={`${id}-hint`}
              aria-invalid={reveal.isError || undefined}
              value={pin}
              onChange={(e) => {
                setPin(e.target.value.replace(/\D/g, "").slice(0, 6));
                reveal.reset();
              }}
              data-autofocus
              data-bwignore="true"
              data-1p-ignore="true"
              data-lpignore="true"
              className={`${inputClass} font-mono tracking-[0.3em]`}
            />
            <p id={`${id}-hint`} className="text-sm text-muted">
              4 to 6 digits
            </p>
            {reveal.isError && <FormError>{errorText(reveal.error)}</FormError>}
          </div>
          <fieldset className="flex min-w-0 flex-col gap-1">
            <legend className={`${labelClass} mb-1`}>Hide again after</legend>
            <div className="flex rounded-[10px] border border-line bg-surface-2 p-0.5">
              {timeouts.map((t) => (
                <label key={t} className={segmentClass}>
                  <input type="radio" name={`${id}-timeout`} value={t} checked={choice === t} onChange={() => setChoice(t)} className="sr-only" />
                  {timeoutLabel(t)}
                </label>
              ))}
            </div>
          </fieldset>
          <div className="flex gap-3">
            <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
              Cancel
            </button>
            <button type="submit" disabled={!complete || reveal.isPending} className={`${primaryButtonClass} flex-1`}>
              Open
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
