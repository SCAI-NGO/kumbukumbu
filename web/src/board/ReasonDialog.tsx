// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { inputClass, labelClass } from "../components/form";
import FormDialog from "./FormDialog";

/** The reason a move into a `reason` column takes (waiting for whom, blocked by what); the card keeps it until its next move. */
export default function ReasonDialog({ card, column, onCancel, onMove }: { card: string; column: string; onCancel(): void; onMove(reason: string): Promise<unknown> }) {
  const id = useId();
  const [reason, setReason] = useState("");
  return (
    <FormDialog title={`Why does ${card} go to ${column}?`} submitLabel="Move" ready={!!reason.trim()} onCancel={onCancel} onSubmit={() => onMove(reason.trim())}>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={id} className={labelClass}>
          Reason
        </label>
        <input id={id} data-autofocus value={reason} onChange={(e) => setReason(e.target.value)} autoComplete="off" className={inputClass} />
      </div>
      <p className="text-sm text-muted">The card keeps the reason until it moves on.</p>
    </FormDialog>
  );
}
