// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { FilePlus, FolderPlus } from "lucide-react";
import { useEffect, useRef, useState, type ClipboardEvent, type DragEvent } from "react";
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { inputClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { collectFromDrop, collectFromInput, type Picked } from "./collectFiles";
import ConflictList from "./ConflictList";
import { ACCEPT, isAttachmentName, MAX_ATTACHMENT_BYTES, planUpload, toBatches, TOO_LARGE_FILE, type Skipped, type UploadFile } from "./planUpload";
import { putFile } from "./putFile";
import { shrinkImage, ShrinkError, shrinkType, shrunkName, type ShrinkType } from "./shrinkImage";
import { useHoldUpdates } from "../update/apply";

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * "34 notes uploaded, 3 replaced, 12 files skipped"; with `files` (a pick that held images or PDFs), "34 notes and
 * 5 files uploaded, …".
 */
export const summaryText = ({ written, files, replaced, skipped }: { written: number; files?: number; replaced: number; skipped: number }) =>
  `${count(written, "note", "notes")}${files === undefined ? "" : ` and ${count(files, "file", "files")}`} uploaded, ${replaced} replaced, ${count(skipped, "file", "files")} skipped`;

/** The question for images over 15 MB (attachments spec, "Formats and limits"). */
export const SHRINK_QUESTION = "Too large for Kumbu² (15 MB). Shrink it so it fits?";

/** What shrinking does to an image beyond its size: a new extension, a GIF's animation lost. */
export function shrinkNotices(path: string, type: ShrinkType): string[] {
  const name = path.slice(path.lastIndexOf("/") + 1);
  const renamed = shrunkName(name, type);
  return [
    ...(renamed !== name ? [`It is uploaded as ${renamed}.`] : []),
    ...(/\.gif$/i.test(name) ? ["An animated GIF loses its animation."] : []),
  ];
}

/** The formats an image pasted from the clipboard may have, with the extension its name gets. */
const pastedExtensions: Record<string, string> = { "image/png": ".png", "image/jpeg": ".jpg", "image/gif": ".gif", "image/webp": ".webp" };

const pad = (n: number) => String(n).padStart(2, "0");

/** "pasted 2026-10-27 22-15.png", in local time. */
export const pastedName = (at: Date, extension: string) =>
  `pasted ${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ${pad(at.getHours())}-${pad(at.getMinutes())}${extension}`;

/** The name typed for a pasted image, with its format's extension added unless it has it. */
function withExtension(name: string, extension: string) {
  const has = extension === ".jpg" ? /\.jpe?g$/i : new RegExp(`\\${extension}$`, "i");
  return has.test(name) ? name : `${name}${extension}`;
}

type OnConflict = "ask" | "replace" | "skip";

/** A request: a batch of notes, or one image or PDF (`version`: replace the file at that version). */
type Step = { kind: "notes"; files: UploadFile[]; onConflict: OnConflict } | { kind: "file"; path: string; file: Blob; version?: string };

/** An image or PDF that exists already, with the version the user is asked about replacing. */
interface FileConflict {
  path: string;
  file: Blob;
  version: string;
}

/** An upload under way: what is sent, what came back, and what waits for the user's choice. */
interface Run {
  steps: Step[];
  written: number;
  filesWritten: number;
  replaced: number;
  skipped: Skipped[];
  /** The files of `ask` requests that met existing notes: the server wrote none of them. */
  held: UploadFile[];
  conflicts: string[];
  fileConflicts: FileConflict[];
  /** The pick held images or PDFs: the summary counts them too. */
  withFiles: boolean;
}

/** What a pick turned into while it is read (and its large images shrunk), before the run is made of it. */
interface Pending {
  notes: UploadFile[][];
  files: Picked[];
  skipped: Skipped[];
  withFiles: boolean;
  /** A pasted image goes straight on when nothing was skipped: its name was the one question. */
  start: boolean;
}

type View =
  | { kind: "pick"; error?: string }
  | { kind: "paste"; file: File; extension: string; name: string }
  | { kind: "reading" }
  | { kind: "shrink"; images: Picked[]; type: ShrinkType }
  | { kind: "shrinking"; path: string }
  | { kind: "review"; notes: number; files: number }
  | { kind: "running"; at: number }
  | { kind: "conflicts" }
  | { kind: "failed"; at: number; error: string }
  | { kind: "done" };

const emptyRun = (): Run => ({ steps: [], written: 0, filesWritten: 0, replaced: 0, skipped: [], held: [], conflicts: [], fileConflicts: [], withFiles: false });

const NOTE_EXISTS = "a note exists at this path";
const FILE_EXISTS = "a file exists at this path";
const CHANGED = "changed after you chose to replace it; kept as it is";

// Notes and files appear and change: the tree, the counts, search and any open note are asked again.
const touched = (vault: string) => [["tree", vault], ["vaults"], ["search"], ["note"], ["links"], ["backlinks"], ["history"], ["changes"]];

const pickButtonClass = `${secondaryButtonClass} flex flex-1 cursor-pointer items-center justify-center gap-2 focus-within:outline-2 focus-within:outline-accent`;

function reviewText(notes: number, files: number) {
  if (notes === 0 && files === 0) return "Nothing to upload";
  if (files === 0) return `${count(notes, "Markdown file", "Markdown files")} to upload`;
  if (notes === 0) return `${count(files, "file", "files")} to upload`;
  return `${count(notes, "Markdown file", "Markdown files")} and ${count(files, "other file", "other files")} to upload`;
}

export function SkippedList({ skipped }: { skipped: Skipped[] }) {
  if (skipped.length === 0) return null;
  return (
    <details className="rounded-[10px] border border-line bg-surface-2 px-3 py-2">
      <summary className="flex min-h-11 cursor-pointer items-center text-sm text-text-body">{count(skipped.length, "file", "files")} skipped</summary>
      <ul aria-label="Skipped files" className="mt-1.5 flex max-h-48 flex-col gap-1.5 overflow-y-auto">
        {skipped.map((s, i) => (
          <li key={`${i}${s.path}`} className="flex flex-col">
            <span className="truncate font-mono text-[12.5px] text-text">{s.path}</span>
            <span className="text-[12.5px] text-muted">{s.reason}</span>
          </li>
        ))}
      </ul>
    </details>
  );
}

/**
 * Markdown files, images, PDFs and folders into a folder of a vault (accounts design §4, "Upload"; attachments spec,
 * "Upload"). Notes are read here, in batches the server takes, and first sent with `ask`: a batch that meets existing
 * notes writes nothing, and the user decides about those notes before anything of that batch is written. Images and
 * PDFs go one `PUT` each, only to a free path at first: one that exists is asked about with the notes, and replaced
 * only at the version the server reported. An image over 15 MB is offered for shrinking first. An image pasted
 * (Ctrl/Cmd+V) gets a name the user can change. `folder`: `/Work/projects`. It is the notes view's overlay unless
 * `onClose` is given, as the gallery does, which keeps it open over itself.
 */
export default function UploadSheet({ folder, dropped, onClose }: { folder: string; dropped: Picked[] | null; onClose?: () => void }) {
  const setOverlay = useEditor((s) => s.setOverlay);
  const queryClient = useQueryClient();
  const [view, setView] = useState<View>({ kind: "pick" });
  const run = useRef<Run>(emptyRun());
  const pending = useRef<Pending | null>(null);
  const [vault, ...rest] = folder.split("/").slice(1);
  const inVault = rest.join("/");
  // A batch in flight is finished first: closing then would leave the user without the summary. Conflicts need a
  // choice: closing would drop the held new notes of their batches without a word.
  const busy = view.kind === "reading" || view.kind === "shrinking" || view.kind === "running" || view.kind === "conflicts";
  // A reload into a new release would cut the upload off between batches: it waits.
  useHoldUpdates(busy);
  const close = () => {
    if (busy) return;
    if (onClose) onClose();
    else setOverlay("none");
  };
  const dialog = useDialog<HTMLDivElement>(close);

  async function take(picked: Picked[], start = false) {
    setView({ kind: "reading" });
    const plan = planUpload(picked.map((p) => ({ ...p, size: p.file.size })));
    let notes: UploadFile[];
    try {
      notes = await Promise.all(plan.notes.map(async (k) => ({ path: k.path, content: await k.file.text() })));
    } catch {
      setView({ kind: "pick", error: "The files couldn't be read. Choose them again." });
      return;
    }
    const { batches, skipped } = toBatches(notes);
    pending.current = {
      notes: batches,
      files: plan.files,
      skipped: [...plan.skipped, ...skipped],
      withFiles: picked.some((p) => isAttachmentName(p.path.slice(p.path.lastIndexOf("/") + 1))),
      start,
    };
    if (plan.tooLarge.length) setView({ kind: "shrink", images: plan.tooLarge, type: shrinkType() });
    else ready();
  }

  /** The images over 15 MB, shrunk one after the other on yes, or skipped. */
  async function shrink(images: Picked[], yes: boolean) {
    const p = pending.current!;
    for (const image of images) {
      if (!yes) {
        p.skipped.push({ path: image.path, reason: TOO_LARGE_FILE });
        continue;
      }
      setView({ kind: "shrinking", path: image.path });
      try {
        const small = await shrinkImage(image.file, MAX_ATTACHMENT_BYTES);
        const slash = image.path.lastIndexOf("/");
        p.files.push({ path: image.path.slice(0, slash + 1) + shrunkName(image.path.slice(slash + 1), small.type as ShrinkType), file: small });
      } catch (err) {
        p.skipped.push({ path: image.path, reason: err instanceof ShrinkError ? err.message : "this browser couldn't shrink it" });
      }
    }
    ready();
  }

  /** The run made of what was read: notes first, then the files. */
  function ready() {
    const p = pending.current!;
    pending.current = null;
    const steps: Step[] = [
      ...p.notes.map((files): Step => ({ kind: "notes", files, onConflict: "ask" })),
      ...p.files.map((f): Step => ({ kind: "file", path: f.path, file: f.file })),
    ];
    run.current = { ...emptyRun(), steps, skipped: p.skipped, withFiles: p.withFiles };
    if (p.start && p.skipped.length === 0 && steps.length > 0) void runFrom(0);
    else setView({ kind: "review", notes: p.notes.flat().length, files: p.files.length });
  }

  const refresh = () => {
    for (const queryKey of touched(vault)) void queryClient.invalidateQueries({ queryKey });
  };

  async function sendNotes(step: Extract<Step, { kind: "notes" }>) {
    const r = run.current;
    const result = await call(api.POST("/api/upload/{vault}/{folder}", { params: { path: { vault, folder: inVault } }, body: { files: step.files, onConflict: step.onConflict } }));
    r.written += Number(result.written);
    r.replaced += Number(result.replaced);
    r.skipped.push(...result.skipped);
    if (result.conflicts.length) {
      const refused = new Set(result.skipped.map((s) => s.path));
      r.held.push(...step.files.filter((f) => !refused.has(f.path)));
      r.conflicts.push(...result.conflicts.map((c) => c.path));
    }
  }

  async function sendFile(step: Extract<Step, { kind: "file" }>) {
    const r = run.current;
    let result;
    try {
      result = await putFile(vault, inVault ? `${inVault}/${step.path}` : step.path, new Uint8Array(await step.file.arrayBuffer()), step.version);
    } catch (err) {
      // A refusal of this file alone (its format, content or size; its path a folder or above a note, 409 without a
      // file state; replaced, it was deleted meanwhile): listed, and the rest goes on.
      const alone =
        err instanceof HttpError && (err.status === 400 || err.status === 409 || err.status === 413 || (err.status === 404 && step.version !== undefined));
      if (!alone) throw err;
      r.skipped.push({ path: step.path, reason: errorText(err) });
      return;
    }
    if (result.kind === "conflict") {
      if (step.version === undefined) r.fileConflicts.push({ path: step.path, file: step.file, version: result.current.version });
      else r.skipped.push({ path: step.path, reason: CHANGED });
    } else if (result.kind === "created") r.filesWritten++;
    else r.replaced++;
  }

  async function runFrom(from: number) {
    const r = run.current;
    for (let i = from; i < r.steps.length; i++) {
      setView({ kind: "running", at: i });
      const step = r.steps[i];
      try {
        if (step.kind === "notes") await sendNotes(step);
        else await sendFile(step);
      } catch (err) {
        refresh();
        setView({ kind: "failed", at: i, error: errorText(err) });
        return;
      }
    }
    refresh();
    setView(r.conflicts.length || r.fileConflicts.length ? { kind: "conflicts" } : { kind: "done" });
  }

  /**
   * The user's choice about the existing notes and files: the chosen notes go with `replace`, the held new ones with
   * `skip`, so a note that appeared meanwhile isn't replaced unasked either. A chosen file is replaced only at the
   * version it had when asked about.
   */
  function decide(replace: Set<string>) {
    const r = run.current;
    const conflicting = new Set(r.conflicts);
    const fresh = r.held.filter((f) => !conflicting.has(f.path));
    const chosen = r.held.filter((f) => replace.has(f.path));
    r.skipped.push(
      ...r.conflicts.filter((p) => !replace.has(p)).map((path) => ({ path, reason: NOTE_EXISTS })),
      ...r.fileConflicts.filter((f) => !replace.has(f.path)).map(({ path }) => ({ path, reason: FILE_EXISTS })),
    );
    r.steps = [
      ...toBatches(fresh).batches.map((files): Step => ({ kind: "notes", files, onConflict: "skip" })),
      ...toBatches(chosen).batches.map((files): Step => ({ kind: "notes", files, onConflict: "replace" })),
      ...r.fileConflicts.filter((f) => replace.has(f.path)).map((f): Step => ({ kind: "file", ...f })),
    ];
    r.held = [];
    r.conflicts = [];
    r.fileConflicts = [];
    void runFrom(0);
  }

  // Files dropped on the note open the sheet with them, read once (StrictMode runs effects twice).
  const took = useRef(false);
  useEffect(() => {
    if (!dropped || took.current) return;
    took.current = true;
    void take(dropped);
  }, []);

  // Each step replaces the controls the focus was on: it moves to the step's default (Skip all, Retry, Done).
  useEffect(() => {
    (dialog.current?.querySelector<HTMLElement>("[data-autofocus]") ?? dialog.current)?.focus();
  }, [view.kind, dialog]);

  // A drop that misses the drop zone would make the browser open the file in place of the app, run and all.
  useEffect(() => {
    const swallow = (e: globalThis.DragEvent) => {
      if (e.defaultPrevented) return;
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = "none";
    };
    window.addEventListener("dragover", swallow);
    window.addEventListener("drop", swallow);
    return () => {
      window.removeEventListener("dragover", swallow);
      window.removeEventListener("drop", swallow);
    };
  }, []);

  const onDragOver = (e: DragEvent) => {
    if (view.kind === "pick" && e.dataTransfer.types.includes("Files")) e.preventDefault();
  };
  const onDrop = (e: DragEvent) => {
    if (view.kind !== "pick") return;
    e.preventDefault();
    void collectFromDrop(e.dataTransfer.items).then((picked) => take(picked));
  };

  // An image pasted (a screenshot on the desktop) is named after the moment; the name can be changed before it goes.
  const onPaste = (e: ClipboardEvent) => {
    if (view.kind !== "pick") return;
    const file = Array.from(e.clipboardData?.files ?? [])[0];
    if (!file) return;
    e.preventDefault();
    const extension = pastedExtensions[file.type];
    if (!extension) setView({ kind: "pick", error: "Only images (PNG, JPEG, GIF, WebP) can be pasted here." });
    else setView({ kind: "paste", file, extension, name: pastedName(new Date(), extension) });
  };
  const uploadPasted = (paste: Extract<View, { kind: "paste" }>) => {
    const name = withExtension(paste.name.trim().normalize("NFC"), paste.extension);
    void take([{ path: name, file: new File([paste.file], name, { type: paste.file.type }) }], true);
  };

  const r = run.current;
  const totals = { written: r.written, files: r.withFiles ? r.filesWritten : undefined, replaced: r.replaced, skipped: r.skipped.length };
  const steps = r.steps.length;
  const onlyNotes = r.steps.every((s) => s.kind === "notes");
  const stepText = (at: number) => `${onlyNotes ? "Batch" : "Step"} ${at + 1} of ${steps}`;

  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="upload-title"
        tabIndex={-1}
        onDragOver={onDragOver}
        onDrop={onDrop}
        onPaste={onPaste}
        className="k-sheet flex max-h-full w-full max-w-md flex-col gap-3 overflow-y-auto rounded-[16px] border border-line bg-surface p-[18px]"
      >
        <header>
          <h2 id="upload-title" className="text-[20px] font-bold text-text">
            Upload
          </h2>
          <p className="font-mono text-[12px] text-dim">Into {[vault, ...rest].join(" / ")}</p>
        </header>

        {view.kind === "pick" && (
          <>
            <p className="text-text-body">
              Markdown files (.md, .markdown) become notes; PDFs and images (.pdf, .png, .jpg, .jpeg, .gif, .webp) up to 15 MB are kept as they are; other files are
              skipped. A folder comes in with its own name. You can paste an image too.
            </p>
            {view.error && (
              <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
                {view.error}
              </p>
            )}
            <div className="flex flex-wrap gap-2.5">
              <label className={pickButtonClass}>
                <input type="file" multiple accept={ACCEPT} className="sr-only" onChange={(e) => take(collectFromInput(e.target.files ?? []))} />
                <FilePlus size={18} aria-hidden className="text-muted" />
                Choose files
              </label>
              <label className={pickButtonClass}>
                <input type="file" multiple className="sr-only" {...{ webkitdirectory: "" }} onChange={(e) => take(collectFromInput(e.target.files ?? []))} />
                <FolderPlus size={18} aria-hidden className="text-muted" />
                Choose a folder
              </label>
            </div>
            <button type="button" onClick={close} className={secondaryButtonClass}>
              Cancel
            </button>
          </>
        )}

        {view.kind === "paste" && (
          <form
            className="flex flex-col gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              if (view.name.trim()) uploadPasted(view);
            }}
          >
            <label className="flex flex-col gap-1.5 text-sm text-text-body">
              Name of the pasted image
              <input data-autofocus value={view.name} onChange={(e) => setView({ ...view, name: e.target.value })} className={inputClass} />
            </label>
            <div className="flex gap-2.5">
              <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              <button type="submit" disabled={!view.name.trim()} className={`${primaryButtonClass} flex-1`}>
                Upload
              </button>
            </div>
          </form>
        )}

        {view.kind === "reading" && <p role="status" className="text-text-body">Reading the files …</p>}

        {view.kind === "shrink" && (
          <>
            <p className="text-text">{SHRINK_QUESTION}</p>
            <ul aria-label="Images too large" className="flex flex-col gap-1.5">
              {view.images.map((image) => (
                <li key={image.path} className="flex flex-col">
                  <span className="truncate font-mono text-[12.5px] text-text">{image.path}</span>
                  {shrinkNotices(image.path, view.type).map((notice) => (
                    <span key={notice} className="text-[12.5px] text-muted">
                      {notice}
                    </span>
                  ))}
                </li>
              ))}
            </ul>
            <div className="flex gap-2.5">
              <button type="button" onClick={() => void shrink(view.images, false)} className={`${secondaryButtonClass} flex-1`}>
                Skip {view.images.length === 1 ? "it" : "them"}
              </button>
              <button type="button" data-autofocus onClick={() => void shrink(view.images, true)} className={`${primaryButtonClass} flex-1`}>
                Shrink
              </button>
            </div>
          </>
        )}

        {view.kind === "shrinking" && <p role="status" className="text-text-body">{`Shrinking ${view.path} …`}</p>}

        {view.kind === "review" && (
          <>
            <p className="text-text">
              {reviewText(view.notes, view.files)}, {count(r.skipped.length, "file", "files")} skipped
            </p>
            <SkippedList skipped={r.skipped} />
            <div className="flex gap-2.5">
              <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              <button type="button" data-autofocus disabled={steps === 0} onClick={() => runFrom(0)} className={`${primaryButtonClass} flex-1`}>
                Upload
              </button>
            </div>
          </>
        )}

        {view.kind === "running" && (
          <div role="status" className="flex flex-col gap-2">
            <p className="text-text">{stepText(view.at)}</p>
            <div role="progressbar" aria-label="Upload progress" aria-valuemin={0} aria-valuemax={steps} aria-valuenow={view.at} className="h-2 overflow-hidden rounded-full bg-surface-2">
              <div className="h-full bg-accent motion-safe:transition-[width]" style={{ width: `${(view.at / steps) * 100}%` }} />
            </div>
          </div>
        )}

        {view.kind === "conflicts" && <ConflictList paths={[...r.conflicts, ...r.fileConflicts.map((f) => f.path)]} waiting={r.held.length - r.conflicts.length} onDecide={decide} />}

        {view.kind === "failed" && (
          <>
            <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
              {view.error}
            </p>
            <p className="text-text-body">{`${stepText(view.at)} failed. So far: ${summaryText(totals)}`}</p>
            <div className="flex gap-2.5">
              <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
                Close
              </button>
              <button type="button" data-autofocus onClick={() => runFrom(view.at)} className={`${primaryButtonClass} flex-1`}>
                Retry
              </button>
            </div>
          </>
        )}

        {view.kind === "done" && (
          <>
            <p role="status" className="text-text">
              {summaryText(totals)}
            </p>
            <SkippedList skipped={r.skipped} />
            <button type="button" data-autofocus onClick={close} className={primaryButtonClass}>
              Done
            </button>
          </>
        )}
      </div>
    </div>
  );
}
