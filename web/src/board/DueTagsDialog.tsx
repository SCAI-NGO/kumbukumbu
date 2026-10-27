// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import type { BoardCard, UpdateCard } from "../api/board";
import { inputClass, labelClass } from "../components/form";
import FormDialog, { parseTags } from "./FormDialog";

/**
 * What an update sends for the fields shown: a changed due date or tag list, `clearDue`/`clearTags` for one the
 * user emptied, nothing for one left as it was. Null when nothing changed.
 */
export function updateOf(card: BoardCard, due: string, tags: string): UpdateCard | null {
  const update: UpdateCard = { card: card.title };
  if (due !== (card.due ?? "")) {
    if (due) update.due = due;
    else update.clearDue = true;
  }
  const list = parseTags(tags);
  if (list.join("\n") !== card.tags.join("\n")) {
    if (list.length) update.tags = list;
    else update.clearTags = true;
  }
  return Object.keys(update).length > 1 ? update : null;
}

/** "Due and tags…": the card's due date (empty for none) and its tags. */
export default function DueTagsDialog({ card, onCancel, onSave }: { card: BoardCard; onCancel(): void; onSave(update: UpdateCard): Promise<unknown> }) {
  const id = useId();
  const [due, setDue] = useState(card.due ?? "");
  const [tags, setTags] = useState(card.tags.join(", "));
  const update = updateOf(card, due, tags);
  return (
    <FormDialog title={`Due and tags of ${card.title}`} submitLabel="Save" ready={update !== null} onCancel={onCancel} onSubmit={() => onSave(update!)}>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-due`} className={labelClass}>
          Due
        </label>
        <input id={`${id}-due`} data-autofocus type="date" value={due} onChange={(e) => setDue(e.target.value)} className={inputClass} />
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-tags`} className={labelClass}>
          Tags
        </label>
        <input id={`${id}-tags`} value={tags} onChange={(e) => setTags(e.target.value)} autoComplete="off" autoCapitalize="none" spellCheck={false} className={inputClass} />
      </div>
      <p className="text-sm text-muted">An empty field removes the due date or the tags. Tags go between commas or spaces.</p>
    </FormDialog>
  );
}
