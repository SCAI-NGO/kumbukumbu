// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/**
 * The reason a move into a `reason` column takes (waiting for whom, blocked by what); the card keeps it until its
 * next move. Cancel sends nothing; a refused move stays open with the server's sentence.
 */
export default function ReasonDialog({ card, column, onCancel, onMove }: { card: string; column: string; onCancel(): void; onMove(reason: string): Promise<unknown> }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onCancel);
  const [reason, setReason] = useState("");
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!reason.trim()) return;
    setPending(true);
    setProblem(null);
    try {
      await onMove(reason.trim());
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
          Why does {card} go to {column}?
        </h2>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-reason`} className={labelClass}>
            Reason
          </label>
          <input id={`${id}-reason`} data-autofocus value={reason} onChange={(e) => setReason(e.target.value)} autoComplete="off" className={inputClass} />
        </div>
        <p className="text-sm text-muted">The card keeps the reason until it moves on.</p>
        {problem && <FormError>{problem}</FormError>}
        <div className="flex gap-2.5">
          <button type="button" onClick={onCancel} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={pending || !reason.trim()} className={`${primaryButtonClass} flex-1`}>
            Move
          </button>
        </div>
      </form>
    </div>
  );
}
