// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent, type ReactNode } from "react";
import { errorText } from "../api/error-text";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/**
 * The board's small modal forms (a reason, a column's name, a card's due date and tags): Cancel sends nothing; a
 * refusal keeps the dialog open with the server's sentence. `onSubmit` closes it from outside once it succeeds.
 */
export default function FormDialog(props: { title: string; submitLabel: string; ready: boolean; onCancel(): void; onSubmit(): Promise<unknown>; children: ReactNode }) {
  const { title, submitLabel, ready, onCancel, onSubmit, children } = props;
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onCancel);
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!ready) return;
    setPending(true);
    setProblem(null);
    try {
      await onSubmit();
    } catch (err) {
      setProblem(errorText(err));
      setPending(false);
    }
  };

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={(e) => void submit(e)}
        className="k-sheet flex w-full max-w-md flex-col gap-4 rounded-[16px] border border-line bg-surface p-[18px] pb-[max(18px,env(safe-area-inset-bottom))]"
      >
        <h2 id={`${id}-title`} className="text-[20px] font-bold break-words text-text">
          {title}
        </h2>
        {children}
        {problem && <FormError>{problem}</FormError>}
        <div className="flex gap-2.5">
          <button type="button" onClick={onCancel} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={pending || !ready} className={`${primaryButtonClass} flex-1`}>
            {submitLabel}
          </button>
        </div>
      </form>
    </div>
  );
}

/** "docs, #urgent  later" → ["docs", "urgent", "later"]: commas or spaces between, `#` optional, each once. */
export const parseTags = (text: string) => [...new Set(text.split(/[\s,]+/).map((t) => t.replace(/^#+/, "")).filter(Boolean))];
