// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import {
  BundleChangedError,
  manyNotes,
  maxDepth,
  pdfsNotYet,
  previewSet,
  usePasswordWindowOpen,
  usePublicationPreview,
  usePublish,
  useUpdatePublication,
  type PublicationPreviewView,
} from "../api/publications";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { CopyButton } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";

const reasons: Record<string, string> = {
  unpublishable: "marked unpublishable",
  behind: "only reached through a note that stays out",
  without: "taken out by you",
};

const notesText = (n: number) => (n === 1 ? "1 note" : `${n} notes`);

// The Take back in button: the secondary button, 48 px tall as a touch target in a list.
const takeBackClass = secondaryButtonClass.replace("min-h-11", "min-h-12");

/** The control that takes the focus once the new list shows it: the note's box, or its Take back in button. */
type Focus = { path: string; on: "box" | "back" };

const megabytes = (bytes: number) => `${(bytes / (1024 * 1024)).toFixed(bytes < 10 * 1024 * 1024 ? 1 : 0)} MB`;

/**
 * The list a publication takes (Publish 2.0 decision 2): every note with a box to take it out (the start note stays),
 * then what stays behind and why: the notes left out, an unticked one with a button to take it back in, and the PDFs the
 * notes embed. Nothing is published before this list was on screen.
 */
function BundleList({
  preview,
  start,
  without,
  focus,
  onToggle,
  onFocused,
}: {
  preview: PublicationPreviewView;
  start: string;
  without: readonly string[];
  focus: Focus | null;
  onToggle(path: string, then: Focus["on"]): void;
  onFocused(): void;
}) {
  const id = useId();
  // The list unmounts while the next preview loads, and the focused control with it: the focus goes to what the note
  // became, if the new list shows it.
  const controls = useRef(new Map<string, HTMLElement>());
  const control = (key: string) => (e: HTMLElement | null) => {
    if (e) controls.current.set(key, e);
    else controls.current.delete(key);
  };
  useEffect(() => {
    if (!focus) return;
    controls.current.get(`${focus.on}:${focus.path}`)?.focus();
    onFocused();
  });
  const count = preview.notes.length;
  const many = count >= manyNotes;
  // A path taken out that the preview doesn't name is listed too, so that it can be taken back in.
  const behind = [
    ...preview.leftOut,
    ...without.filter((w) => !preview.notes.some((n) => n.path === w) && !preview.leftOut.some((l) => l.path === w)).map((path) => ({ path, reason: "without" })),
  ];
  const pdfs = preview.plain.filter((p) => p !== pdfsNotYet && p.toLowerCase().endsWith(".pdf"));
  return (
    <div className="flex flex-col gap-3">
      <p role="status" className={`text-sm ${many ? "font-semibold text-warning" : "text-text-body"}`}>
        {notesText(count)}
        {Number(preview.bytes) > 0 ? `, ${megabytes(Number(preview.bytes))} of images` : ""}
        {many && <span className="block">That's a lot of notes. Check the list before you publish.</span>}
      </p>
      <fieldset className="flex max-h-64 flex-col gap-1 overflow-y-auto rounded-[10px] border border-line p-2">
        <legend className="px-1 text-sm text-text-body">What goes out</legend>
        {preview.notes.map((n) => (
          <label key={n.path} className="flex min-h-9 items-center gap-2 text-sm text-text">
            <input type="checkbox" checked disabled={n.path === start} onChange={() => onToggle(n.path, "back")} ref={control(`box:${n.path}`)} />
            <span className="min-w-0 break-words">
              {n.title} <span className="font-mono text-[12px] text-muted">{n.path}</span>
            </span>
          </label>
        ))}
      </fieldset>
      {behind.length + pdfs.length > 0 && (
        <div>
          <h3 id={`${id}-left`} className="text-sm font-semibold text-text">
            Stays behind
          </h3>
          <ul aria-labelledby={`${id}-left`} className="flex flex-col gap-1.5 text-sm text-text-body">
            {behind.map((l, i) => (
              // The button under its line always, not beside it when the path is short: a list of them stays even.
              <li key={l.path} className="flex flex-col items-start gap-1 break-words">
                <span id={`${id}-behind-${i}`} className="min-w-0">
                  <span className="font-mono text-[12px]">{l.path}</span>: {reasons[l.reason] ?? l.reason}
                </span>
                {l.reason === "without" && (
                  <button
                    type="button"
                    ref={control(`back:${l.path}`)}
                    aria-describedby={`${id}-behind-${i}`}
                    onClick={() => onToggle(l.path, "box")}
                    className={takeBackClass}
                  >
                    Take back in
                  </button>
                )}
              </li>
            ))}
            {pdfs.map((name) => (
              <li key={`pdf:${name}`} className="break-words">
                <span>
                  <span className="font-mono text-[12px]">{name}</span>: a PDF (not published)
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {preview.tooMany && <FormError>More notes than one publication can take: choose a smaller depth or take notes out.</FormError>}
      {preview.tooLarge && <FormError>The images of these notes are larger than 64 MB together: choose a smaller depth or take notes out.</FormError>}
    </div>
  );
}

/**
 * The owner publishes the note (publish spec §3.5, §3.11), with the notes it links to as deep as she chooses (Publish
 * 2.0): her password, as for an invitation, unless the password window is open. With `update`, the same list makes a
 * new copy under the same link. The link stays in this component's state; the bar above the note copies it again later.
 */
export default function PublishDialog({ path, onClose, update }: { path: string; onClose(): void; update?: { depth: number; without: string[] } }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const passwordWindow = usePasswordWindowOpen();
  // The field's own state, cleared once used. The mutation holds it as its variables until it settles, then is reset
  // (the link, its answer, too): the refusal is kept here instead.
  const [password, setPassword] = useState("");
  const [depth, setDepth] = useState(update?.depth ?? 0);
  const [without, setWithout] = useState<string[]>(update?.without ?? []);
  const [changed, setChanged] = useState<PublicationPreviewView | null>(null);
  const [focus, setFocus] = useState<Focus | null>(null);
  const [published, setPublished] = useState<{ link: string; plain: string[] } | null>(null);
  const [refusal, setRefusal] = useState<Error | null>(null);
  const [copyFailed, setCopyFailed] = useState(false);
  const publish = usePublish(path);
  const updating = useUpdatePublication(path);
  const bundle = depth > 0;
  const asked = usePublicationPreview(path, depth, without, bundle);
  const preview = changed ?? asked.data;
  const asks = !update && passwordWindow.known && !passwordWindow.open;
  const pending = publish.isPending || updating.isPending;
  const blocked = bundle && (!preview || preview.tooMany || preview.tooLarge);
  const disabled = pending || blocked || (!update && (!passwordWindow.known || (asks && password === "")));

  const toggle = (note: string, then: Focus["on"]) => {
    setChanged(null);
    setFocus({ path: note, on: then });
    setWithout((w) => (w.includes(note) ? w.filter((p) => p !== note) : [...w, note]));
  };

  const failed = (error: Error) => {
    if (error instanceof BundleChangedError) setChanged(error.preview);
    setRefusal(error);
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (disabled) return;
    setRefusal(null);
    const expected = bundle && preview ? previewSet(preview) : undefined;
    if (update) {
      updating.mutate(
        { depth, without, expected: expected ?? [path] },
        {
          onSuccess: () => {
            updating.reset();
            onClose();
          },
          onError: (error) => {
            failed(error);
            updating.reset();
          },
        },
      );
      return;
    }
    publish.mutate(
      { ...(asks ? { password } : {}), depth, without, expected },
      {
        onSuccess: (view) => {
          setPassword("");
          setPublished({ link: view.link, plain: view.plain });
          publish.reset();
        },
        onError: (error) => {
          failed(error);
          publish.reset();
        },
      },
    );
  };

  const names = published?.plain.filter((p) => p !== pdfsNotYet) ?? [];
  const count = bundle ? (preview?.notes.length ?? 0) : 1;

  return (
    <div className="absolute inset-0 z-50 flex items-end justify-center bg-black/55 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
          {update ? "Update the published copy" : "Publish this note"}
        </h2>
        {published ? (
          <>
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-link`} className={labelClass}>
                Link
              </label>
              <input id={`${id}-link`} readOnly value={published.link} onFocus={(e) => e.target.select()} className={`${inputClass} font-mono text-sm`} />
              <CopyButton value={published.link} label="Copy link" className={secondaryButtonClass} onResult={(ok) => setCopyFailed(!ok)} />
            </div>
            {copyFailed && (
              <p aria-live="polite" className="text-sm text-text">
                Copying didn't work. Select the link and copy it by hand.
              </p>
            )}
            {names.length > 0 && <p className="text-sm text-text-body">Not copied, plain text instead: {names.join(", ")}.</p>}
            {published.plain.includes(pdfsNotYet) && <p className="text-sm text-text-body">{pdfsNotYet}.</p>}
            <button type="button" onClick={onClose} className={primaryButtonClass}>
              Done
            </button>
          </>
        ) : (
          <form onSubmit={submit} className="flex flex-col gap-4" aria-busy={pending}>
            <p className="text-text-body">
              {bundle
                ? "Anyone with the link can read a copy of these notes as they are now. Links between them stay links; links to other notes become plain text."
                : "Anyone with the link can read a copy of this note as it is now. Links to other notes become plain text."}
            </p>
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-depth`} className={labelClass}>
                How many links deep
              </label>
              <div className="flex items-center gap-3">
                <input
                  id={`${id}-depth`}
                  type="number"
                  inputMode="numeric"
                  min={0}
                  max={maxDepth}
                  value={depth}
                  onChange={(e) => {
                    setChanged(null);
                    // The person is in this field now: a list after a toggle that never came takes no focus later.
                    setFocus(null);
                    setDepth(Math.max(0, Math.min(maxDepth, Math.trunc(Number(e.target.value) || 0))));
                  }}
                  aria-describedby={`${id}-depth-hint`}
                  className={`${inputClass} w-20`}
                />
                <span id={`${id}-depth-hint`} className={`text-sm ${bundle && preview && preview.notes.length >= manyNotes ? "font-semibold text-warning" : "text-text-body"}`}>
                  {!bundle ? "This note only" : preview ? `+${notesText(preview.notes.length - 1)}` : "Counting …"}
                </span>
              </div>
            </div>
            {bundle && preview && <BundleList preview={preview} start={path} without={without} focus={focus} onToggle={toggle} onFocused={() => setFocus(null)} />}
            {asks && <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />}
            {refusal && <FormError>{refusal instanceof BundleChangedError ? refusal.message : errorText(refusal)}</FormError>}
            {asked.error && <FormError>{errorText(asked.error)}</FormError>}
            <div className="flex gap-2">
              <button type="button" onClick={onClose} className={secondaryButtonClass}>
                Cancel
              </button>
              <button type="submit" disabled={disabled} className={primaryButtonClass}>
                {update ? "Update copy" : bundle ? `Publish ${notesText(count)}` : "Publish"}
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
