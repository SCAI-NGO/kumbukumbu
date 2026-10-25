// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/admin-client/src/routes/users/actions.tsx)
import { useId, useState } from "react";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "./form";
import { PasswordField } from "./PasswordField";
import { useDialog } from "../hooks/useDialog";

/** A confirmation that is only possible after typing `word` exactly: for actions that lock someone out, let them back in or delete for good. */
export default function ConfirmTyped({
  title,
  text,
  word,
  action,
  pending,
  error,
  onConfirm,
  onClose,
  password,
  onPasswordChange,
}: {
  title: string;
  text: string;
  word: string;
  action: string;
  pending: boolean;
  error: string | null;
  onConfirm(): void;
  onClose(): void;
  /** Given, the dialog also asks for the current password: the caller keeps it in its own state. */
  password?: string;
  onPasswordChange?(value: string): void;
}) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const [typed, setTyped] = useState("");
  const ready = typed === word && (password === undefined || password !== "");

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={(e) => {
          e.preventDefault();
          if (ready) onConfirm();
        }}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          {title}
        </h2>
        <p className="text-text-body">{text}</p>
        {password !== undefined && (
          <PasswordField label="Your password" value={password} onChange={(v) => onPasswordChange?.(v)} autoComplete="current-password" />
        )}
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-word`} className={labelClass}>
            Type {word} to confirm
          </label>
          <input
            id={`${id}-word`}
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            className={inputClass}
          />
        </div>
        {error && <FormError>{error}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={!ready || pending} className={`${primaryButtonClass} flex-1`}>
            {action}
          </button>
        </div>
      </form>
    </div>
  );
}
