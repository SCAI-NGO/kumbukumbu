// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { useBoardAction } from "../api/board";
import { errorText } from "../api/error-text";
import { FormError, inputClass, labelClass, secondaryButtonClass } from "../components/form";
import { parseTags } from "./FormDialog";

/** "+ Card": a title and an optional text, due date and tags (on a phone behind "More"), at the bottom of the lane. */
export default function AddCard({ vault, column }: { vault: string; column: string }) {
  const id = useId();
  const add = useBoardAction(vault);
  const [open, setOpen] = useState(false);
  const [more, setMore] = useState(false);
  const [title, setTitle] = useState("");
  const [text, setText] = useState("");
  const [due, setDue] = useState("");
  const [tags, setTags] = useState("");

  const close = () => {
    setOpen(false);
    setMore(false);
    setTitle("");
    setText("");
    setDue("");
    setTags("");
    add.reset();
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!title.trim()) return;
    const list = parseTags(tags);
    add.mutate(
      { kind: "add", title: title.trim(), column, ...(text.trim() ? { text } : {}), ...(due ? { due } : {}), ...(list.length ? { tags: list } : {}) },
      { onSuccess: close },
    );
  };

  if (!open) {
    return (
      <button
        type="button"
        aria-label={`Add a card to ${column}`}
        onClick={() => setOpen(true)}
        className="flex min-h-11 flex-none items-center self-start rounded-[8px] px-2 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        + Card
      </button>
    );
  }
  return (
    <form aria-label={`New card in ${column}`} onSubmit={submit} className="flex flex-none flex-col gap-2 rounded-[12px] border border-line bg-surface p-3">
      <label htmlFor={`${id}-title`} className={labelClass}>
        Title
      </label>
      <input id={`${id}-title`} autoFocus value={title} onChange={(e) => setTitle(e.target.value)} autoComplete="off" className={inputClass} />
      <label htmlFor={`${id}-text`} className={labelClass}>
        Text (optional)
      </label>
      <textarea id={`${id}-text`} value={text} onChange={(e) => setText(e.target.value)} rows={3} className={`${inputClass} h-auto py-2`} />
      {!more && (
        <button type="button" aria-expanded={false} onClick={() => setMore(true)} className="min-h-11 self-start px-1 text-sm font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent lg:hidden">
          More
        </button>
      )}
      {/* From the laptop breakpoint on there is room for them at once. */}
      <div className={`flex-col gap-2 ${more ? "flex" : "hidden lg:flex"}`}>
        <label htmlFor={`${id}-due`} className={labelClass}>
          Due (optional)
        </label>
        <input id={`${id}-due`} type="date" value={due} onChange={(e) => setDue(e.target.value)} className={inputClass} />
        <label htmlFor={`${id}-tags`} className={labelClass}>
          Tags (optional)
        </label>
        <input id={`${id}-tags`} value={tags} onChange={(e) => setTags(e.target.value)} autoComplete="off" autoCapitalize="none" spellCheck={false} className={inputClass} />
      </div>
      {add.isError && <FormError>{errorText(add.error)}</FormError>}
      <div className="flex gap-2">
        <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
          Cancel
        </button>
        <button
          type="submit"
          disabled={add.isPending || !title.trim()}
          className="min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
        >
          Add
        </button>
      </div>
    </form>
  );
}
