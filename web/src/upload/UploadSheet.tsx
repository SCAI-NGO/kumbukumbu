// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { FilePlus, FolderPlus } from "lucide-react";
import { useEffect, useRef, useState, type DragEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { collectFromDrop, collectFromInput, type Picked } from "./collectFiles";
import ConflictList from "./ConflictList";
import { planUpload, toBatches, type Skipped, type UploadFile } from "./planUpload";
import { useHoldUpdates } from "../update/apply";

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** "34 notes uploaded, 3 replaced, 12 files skipped". */
export const summaryText = ({ written, replaced, skipped }: { written: number; replaced: number; skipped: number }) =>
  `${count(written, "note", "notes")} uploaded, ${replaced} replaced, ${count(skipped, "file", "files")} skipped`;

type OnConflict = "ask" | "replace" | "skip";

interface Step {
  files: UploadFile[];
  onConflict: OnConflict;
}

/** An upload under way: what is sent, what came back, and what waits for the user's choice. */
interface Run {
  steps: Step[];
  written: number;
  replaced: number;
  skipped: Skipped[];
  /** The files of `ask` requests that met existing notes: the server wrote none of them. */
  held: UploadFile[];
  conflicts: string[];
}

type View =
  | { kind: "pick"; error?: string }
  | { kind: "reading" }
  | { kind: "review"; files: number }
  | { kind: "running"; at: number }
  | { kind: "conflicts" }
  | { kind: "failed"; at: number; error: string }
  | { kind: "done" };

const emptyRun = (): Run => ({ steps: [], written: 0, replaced: 0, skipped: [], held: [], conflicts: [] });

// Notes appear and change: the tree, the counts, search and any open note are asked again.
const touched = (vault: string) => [["tree", vault], ["vaults"], ["search"], ["note"], ["links"], ["backlinks"], ["history"], ["changes"]];

const pickButtonClass = `${secondaryButtonClass} flex flex-1 cursor-pointer items-center justify-center gap-2 focus-within:outline-2 focus-within:outline-accent`;

function SkippedList({ skipped }: { skipped: Skipped[] }) {
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
 * Markdown files and folders into a folder of a vault (accounts design §4, "Upload"). The files are read here, in
 * batches the server takes, and first sent with `ask`: a batch that meets existing notes writes nothing, and the
 * user decides about those notes before anything of that batch is written. `folder`: `/Work/projects`.
 */
export default function UploadSheet({ folder, dropped }: { folder: string; dropped: Picked[] | null }) {
  const setOverlay = useEditor((s) => s.setOverlay);
  const queryClient = useQueryClient();
  const [view, setView] = useState<View>({ kind: "pick" });
  const run = useRef<Run>(emptyRun());
  const [vault, ...rest] = folder.split("/").slice(1);
  const inVault = rest.join("/");
  // A batch in flight is finished first: closing then would leave the user without the summary. Conflicts need a
  // choice: closing would drop the held new notes of their batches without a word.
  const busy = view.kind === "reading" || view.kind === "running" || view.kind === "conflicts";
  // A reload into a new release would cut the upload off between batches: it waits.
  useHoldUpdates(busy);
  const close = () => {
    if (!busy) setOverlay("none");
  };
  const dialog = useDialog<HTMLDivElement>(close);

  async function take(picked: Picked[]) {
    setView({ kind: "reading" });
    const plan = planUpload(picked.map((p) => ({ path: p.path, size: p.file.size, file: p.file })));
    let files: UploadFile[];
    try {
      files = await Promise.all(plan.keep.map(async (k) => ({ path: k.path, content: await k.file.text() })));
    } catch {
      setView({ kind: "pick", error: "The files couldn't be read. Choose them again." });
      return;
    }
    const { batches, skipped } = toBatches(files);
    run.current = { ...emptyRun(), steps: batches.map((b) => ({ files: b, onConflict: "ask" })), skipped: [...plan.skipped, ...skipped] };
    setView({ kind: "review", files: batches.flat().length });
  }

  const refresh = () => {
    for (const queryKey of touched(vault)) void queryClient.invalidateQueries({ queryKey });
  };

  async function runFrom(from: number) {
    const r = run.current;
    for (let i = from; i < r.steps.length; i++) {
      setView({ kind: "running", at: i });
      const step = r.steps[i];
      let result;
      try {
        result = await call(api.POST("/api/upload/{vault}/{folder}", { params: { path: { vault, folder: inVault } }, body: step }));
      } catch (err) {
        refresh();
        setView({ kind: "failed", at: i, error: errorText(err) });
        return;
      }
      r.written += Number(result.written);
      r.replaced += Number(result.replaced);
      r.skipped.push(...result.skipped);
      if (result.conflicts.length) {
        const refused = new Set(result.skipped.map((s) => s.path));
        r.held.push(...step.files.filter((f) => !refused.has(f.path)));
        r.conflicts.push(...result.conflicts.map((c) => c.path));
      }
    }
    refresh();
    setView(r.conflicts.length ? { kind: "conflicts" } : { kind: "done" });
  }

  /**
   * The user's choice about the existing notes: the chosen ones go with `replace`, the held new ones with `skip`,
   * so a note that appeared meanwhile isn't replaced unasked either.
   */
  function decide(replace: Set<string>) {
    const r = run.current;
    const conflicting = new Set(r.conflicts);
    const fresh = r.held.filter((f) => !conflicting.has(f.path));
    const chosen = r.held.filter((f) => replace.has(f.path));
    r.skipped.push(...r.conflicts.filter((p) => !replace.has(p)).map((path) => ({ path, reason: "a note exists at this path" })));
    r.steps = [
      ...toBatches(fresh).batches.map((files): Step => ({ files, onConflict: "skip" })),
      ...toBatches(chosen).batches.map((files): Step => ({ files, onConflict: "replace" })),
    ];
    r.held = [];
    r.conflicts = [];
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
    void collectFromDrop(e.dataTransfer.items).then(take);
  };

  const r = run.current;
  const totals = { written: r.written, replaced: r.replaced, skipped: r.skipped.length };
  const batches = r.steps.length;

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
            <p className="text-text-body">Markdown files (.md, .markdown) become notes; other files are skipped. A folder comes in with its own name.</p>
            {view.error && (
              <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
                {view.error}
              </p>
            )}
            <div className="flex flex-wrap gap-2.5">
              <label className={pickButtonClass}>
                <input type="file" multiple className="sr-only" onChange={(e) => take(collectFromInput(e.target.files ?? []))} />
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

        {view.kind === "reading" && <p role="status" className="text-text-body">Reading the files …</p>}

        {view.kind === "review" && (
          <>
            <p className="text-text">
              {view.files === 0 ? "No Markdown files to upload" : `${count(view.files, "Markdown file", "Markdown files")} to upload`}, {count(r.skipped.length, "file", "files")} skipped
            </p>
            <SkippedList skipped={r.skipped} />
            <div className="flex gap-2.5">
              <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              <button type="button" data-autofocus disabled={view.files === 0} onClick={() => runFrom(0)} className={`${primaryButtonClass} flex-1`}>
                Upload
              </button>
            </div>
          </>
        )}

        {view.kind === "running" && (
          <div role="status" className="flex flex-col gap-2">
            <p className="text-text">{`Batch ${view.at + 1} of ${batches}`}</p>
            <div role="progressbar" aria-label="Upload progress" aria-valuemin={0} aria-valuemax={batches} aria-valuenow={view.at} className="h-2 overflow-hidden rounded-full bg-surface-2">
              <div className="h-full bg-accent motion-safe:transition-[width]" style={{ width: `${(view.at / batches) * 100}%` }} />
            </div>
          </div>
        )}

        {view.kind === "conflicts" && <ConflictList paths={r.conflicts} waiting={r.held.length - r.conflicts.length} onDecide={decide} />}

        {view.kind === "failed" && (
          <>
            <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
              {view.error}
            </p>
            <p className="text-text-body">{`Batch ${view.at + 1} of ${batches} failed. So far: ${summaryText(totals)}`}</p>
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
