// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useMemo, useState, type KeyboardEvent, type RefObject, type SyntheticEvent } from "react";
import { useNavigate } from "react-router-dom";
import { api, call } from "../api/client";
import { useCreateUntitled, useTree } from "../api/notes";
import { inVault, noteUrl, titleOf, vaultOf } from "../api/paths";
import { browserZone, useCalendarPreferences } from "../calendar/preferences";
import { useEditor } from "../state/editor";
import { findTemplates, templateEdit, templatePaths, templatesFolder } from "./templates";
import { apply } from "./textEdit";

const keepFocus = (e: SyntheticEvent) => e.preventDefault();

/**
 * Insert template (workspace spec §4): the vault's templates, filtered as typed, between the textarea and the toolbar
 * as link completion is; the chosen one goes in at the textarea's caret. Without templates, the way to the first one.
 */
export default function TemplateList({
  path,
  textarea,
  onChange,
  onHint,
}: {
  path: string;
  textarea: RefObject<HTMLTextAreaElement | null>;
  onChange(value: string): void;
  onHint(hint: string | null): void;
}) {
  const vault = vaultOf(path);
  const tree = useTree(vault);
  const zone = useCalendarPreferences().data?.zone ?? browserZone();
  const createUntitled = useCreateUntitled();
  const navigate = useNavigate();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const [problem, setProblem] = useState<string | null>(null);
  const [reading, setReading] = useState(false);
  const listId = useId();
  const all = useMemo(() => (tree.data ? templatePaths(tree.data.root, vault, path) : []), [tree.data, vault, path]);
  const shown = useMemo(() => findTemplates(all, vault, query), [all, vault, query]);

  /** Back to the textarea, its selection as it was. */
  const close = () => {
    useEditor.getState().setTemplates(false);
    textarea.current?.focus({ preventScroll: true });
  };

  const insert = async (template: string) => {
    if (reading) return;
    setReading(true);
    setProblem(null);
    try {
      // Read for itself, not through the note's query: a view of the template keeps its text.
      const { content } = await call(api.GET("/api/notes/{vault}/{path}", { params: { path: { vault: vaultOf(template), path: inVault(template) } } }));
      const area = textarea.current;
      if (!area) return;
      const { edit, hint } = templateEdit(area.value, area.selectionStart, area.selectionEnd, content, { title: titleOf(path), now: new Date(), zone });
      useEditor.getState().setTemplates(false);
      apply(area, edit, onChange);
      onHint(hint);
    } catch {
      setProblem("Couldn't read the template. Try again.");
    } finally {
      setReading(false);
    }
  };

  const newTemplate = () =>
    createUntitled.mutate(templatesFolder(vault), {
      onSuccess: (created) => {
        useEditor.getState().openNote(created, "write");
        navigate(noteUrl(created));
      },
      onError: () => setProblem("Couldn't make the template. Try again."),
    });

  const at = Math.min(active, shown.length - 1);
  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    // While an IME composes, Enter and the arrows are the IME's.
    if (e.nativeEvent.isComposing || e.keyCode === 229) return;
    if ((e.key === "ArrowDown" || e.key === "ArrowUp") && shown.length) setActive((i) => (i + (e.key === "ArrowDown" ? 1 : shown.length - 1)) % shown.length);
    else if (e.key === "Enter" && shown[at]) void insert(shown[at].path);
    else if (e.key === "Escape") close();
    else return;
    e.preventDefault();
    e.stopPropagation();
  };

  return (
    <div role="group" aria-label="Insert template" className="mx-2 mb-1.5 flex max-h-[40vh] flex-none flex-col rounded-[12px] border border-line bg-surface-2 p-1 shadow-lg">
      <input
        role="combobox"
        aria-label="Find a template"
        aria-expanded={shown.length > 0}
        aria-controls={listId}
        aria-activedescendant={at >= 0 ? `${listId}-${at}` : undefined}
        autoFocus
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setActive(0);
        }}
        onKeyDown={onKeyDown}
        onBlur={(e) => {
          // Into the textarea or another field it closes, as a menu does; not when the window loses the focus.
          if (e.relatedTarget && !e.currentTarget.parentElement?.contains(e.relatedTarget)) useEditor.getState().setTemplates(false);
        }}
        placeholder="Templates"
        className="h-11 flex-none rounded-[8px] bg-transparent px-3 text-[16px] text-text outline-none placeholder:text-muted"
      />
      {/* There in every state, as the combobox's aria-controls needs, empty where there is nothing to choose. */}
      <ul id={listId} role="listbox" aria-label="Templates" className="min-h-0 overflow-y-auto">
        {shown.map((template, i) => (
          <li
            key={template.path}
            id={`${listId}-${i}`}
            role="option"
            aria-selected={i === at}
            onPointerDown={keepFocus}
            onMouseDown={keepFocus}
            onMouseMove={() => i !== at && setActive(i)}
            onClick={() => void insert(template.path)}
            className={`flex min-h-11 cursor-pointer flex-col justify-center rounded-[8px] px-3 py-1 ${i === at ? "bg-accent/15" : ""}`}
          >
            <span className="truncate text-[15px] text-text">{template.name}</span>
            {template.folder && <span className="truncate font-mono text-[11px] text-dim">{template.folder}</span>}
          </li>
        ))}
      </ul>
      {tree.isPending ? (
        <p className="px-3 py-2 text-[15px] text-muted">Loading…</p>
      ) : tree.isError ? (
        <p className="px-3 py-2 text-[15px] text-muted">Couldn't load the templates.</p>
      ) : all.length === 0 ? (
        <div className="flex flex-col items-start gap-2 px-3 py-2">
          <p className="text-[15px] text-text">No templates yet. Notes in a folder named Templates at the top of this vault show up here.</p>
          <button
            type="button"
            onPointerDown={keepFocus}
            onMouseDown={keepFocus}
            onClick={newTemplate}
            disabled={createUntitled.isPending}
            className="min-h-11 rounded-[10px] bg-accent px-4 text-sm font-semibold text-on-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
          >
            New template
          </button>
        </div>
      ) : (
        shown.length === 0 && <p className="px-3 py-2 text-[15px] text-muted">No template matches</p>
      )}
      {problem && (
        <p role="alert" className="px-3 py-2 text-[15px] text-text">
          {problem}
        </p>
      )}
    </div>
  );
}
