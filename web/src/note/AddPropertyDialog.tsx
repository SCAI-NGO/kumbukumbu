// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type ReactNode } from "react";
import { errorText } from "../api/error-text";
import FormDialog from "../board/FormDialog";
import { inputClass, labelClass } from "../components/form";

/**
 * Why a property wasn't added: a name that isn't a plain key or is there already, properties at the top that can't
 * be read, a value that wouldn't read back as typed, or a sentence after which it may be tried again (a conflict, a
 * failure).
 */
export type AddRefusal = "name" | "exists" | "frontmatter" | "value";
export type AddSaid = { refused: AddRefusal } | { text: string };

/** A refusal shown in the dialog, which stays open. */
class NotAdded extends Error {
  constructor(readonly said: ReactNode) {
    super("not added");
  }
}

const sentenceOf = (said: AddSaid, name: string): ReactNode => {
  if ("text" in said) return said.text;
  switch (said.refused) {
    case "name":
      return "Use letters, digits, - and _, starting with a letter.";
    case "exists":
      return (
        <>
          <code className="rounded-[4px] bg-surface px-[5px] py-px font-mono text-[0.85em]">{name}</code> is already in the properties: change it in the note.
        </>
      );
    case "frontmatter":
      return "The properties at the top can't be read here; change them in Write mode.";
    case "value":
      return "That value can't be written as a property here.";
  }
};

/**
 * Add property (spec §2.2): a name and a value, written by `onAdd` as the properties' last line, which answers why
 * it wasn't, or null once it was; the dialog then closes. The value is text, quoted where YAML would read it otherwise.
 */
export default function AddPropertyDialog({ onAdd, onClose }: { onAdd(name: string, value: string): Promise<AddSaid | null>; onClose(): void }) {
  const id = useId();
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  return (
    <FormDialog
      title="Add property"
      submitLabel="Add"
      ready={name.trim() !== ""}
      onCancel={onClose}
      errorTextOf={(err) => (err instanceof NotAdded ? err.said : errorText(err))}
      onSubmit={async () => {
        const said = await onAdd(name.trim(), value);
        if (said) throw new NotAdded(sentenceOf(said, name.trim()));
        onClose();
      }}
    >
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-name`} className={labelClass}>
          Name
        </label>
        <input
          id={`${id}-name`}
          data-autofocus
          value={name}
          onChange={(e) => setName(e.target.value)}
          autoComplete="off"
          autoCapitalize="none"
          autoCorrect="off"
          spellCheck={false}
          className={`${inputClass} font-mono`}
        />
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-value`} className={labelClass}>
          Value
        </label>
        <input id={`${id}-value`} value={value} onChange={(e) => setValue(e.target.value)} autoComplete="off" className={inputClass} />
      </div>
    </FormDialog>
  );
}
