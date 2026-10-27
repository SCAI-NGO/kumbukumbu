// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { inputClass, labelClass } from "../components/form";
import FormDialog from "./FormDialog";

/** Renames a column: the server changes the definition and every card in it in one go, or nothing. */
export default function RenameColumnDialog({ column, onCancel, onRename }: { column: string; onCancel(): void; onRename(to: string): Promise<unknown> }) {
  const id = useId();
  const [name, setName] = useState(column);
  const to = name.trim();
  return (
    <FormDialog title={`Rename ${column}`} submitLabel="Rename" ready={!!to && to !== column} onCancel={onCancel} onSubmit={() => onRename(to)}>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={id} className={labelClass}>
          New name
        </label>
        <input id={id} data-autofocus value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" maxLength={64} className={inputClass} />
      </div>
      <p className="text-sm text-muted">The cards in it move along with the name.</p>
    </FormDialog>
  );
}
