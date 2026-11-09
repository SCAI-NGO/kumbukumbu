// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { useDialog } from "../hooks/useDialog";
import { useSetPin } from "./api";

const pinPattern = /^[0-9]{4,6}$/;

function PinField({ label, value, onChange, hint, invalid }: { label: string; value: string; onChange(value: string): void; hint?: string; invalid?: boolean }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className={labelClass}>
        {label}
      </label>
      {/* As the PIN dialog's: a password field so that the digits aren't shown, which password managers leave alone. */}
      <input
        id={id}
        type="password"
        inputMode="numeric"
        autoComplete="off"
        maxLength={6}
        value={value}
        onChange={(e) => onChange(e.target.value.replace(/\D/g, "").slice(0, 6))}
        aria-describedby={hint ? `${id}-hint` : undefined}
        aria-invalid={invalid || undefined}
        data-bwignore="true"
        data-1p-ignore="true"
        data-lpignore="true"
        className={`${inputClass} font-mono tracking-[0.3em]`}
      />
      {hint && (
        <p id={`${id}-hint`} className="text-sm text-muted">
          {hint}
        </p>
      )}
    </div>
  );
}

/**
 * Sets the PIN, or changes it: the same dialog and the same answer for everyone, whether a PIN was set before or not
 * (spec §3.1), and always with the current password.
 */
export default function SetPinDialog({ onDone, onClose }: { onDone(): void; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const [password, setPassword] = useState("");
  const [pin, setPin] = useState("");
  const [again, setAgain] = useState("");
  const [mismatch, setMismatch] = useState(false);
  const setPinMutation = useSetPin(password, pin);
  const complete = !!password && pinPattern.test(pin) && again.length > 0;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!complete || setPinMutation.isPending) return;
    if (pin !== again) return setMismatch(true);
    setPinMutation.mutate(undefined, {
      onSuccess: () => {
        setPassword("");
        setPin("");
        setAgain("");
        onDone();
      },
    });
  };

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={submit}
        className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold text-text">
          Set or change PIN
        </h2>
        <PasswordField
          label="Current password"
          value={password}
          onChange={(v) => {
            setPassword(v);
            setPinMutation.reset();
          }}
          autoComplete="current-password"
        />
        <PinField
          label="New PIN"
          value={pin}
          onChange={(v) => {
            setPin(v);
            setMismatch(false);
            setPinMutation.reset();
          }}
          hint="4 to 6 digits"
        />
        <PinField
          label="New PIN again"
          value={again}
          invalid={mismatch}
          onChange={(v) => {
            setAgain(v);
            setMismatch(false);
          }}
        />
        {mismatch && <FormError>The two PINs differ.</FormError>}
        {setPinMutation.isError && <FormError>{errorText(setPinMutation.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={!complete || setPinMutation.isPending} className={`${primaryButtonClass} flex-1`}>
            Set PIN
          </button>
        </div>
      </form>
    </div>
  );
}
