// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { FileArchive } from "lucide-react";
import { useEffect, useId, useState } from "react";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useHoldUpdates } from "../update/apply";
import { isAttachmentName } from "../upload/planUpload";
import { SkippedList } from "../upload/UploadSheet";
import { ImportTooLarge, readImport, vaultKey, type ImportVault } from "./readImport";
import { planImport, runImport, serverImportApi, summaryText, type Choice, type ImportApi, type ImportSummary, type VaultPlan } from "./runImport";

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** "2 notes", "1 file", "2 notes and 1 file": of paths below a vault. */
function itemsText(paths: string[]) {
  const files = paths.filter((p) => isAttachmentName(p.slice(p.lastIndexOf("/") + 1))).length;
  const notes = paths.length - files;
  return files === 0 ? count(notes, "note", "notes") : notes === 0 ? count(files, "file", "files") : `${count(notes, "note", "notes")} and ${count(files, "file", "files")}`;
}

type View =
  | { kind: "pick"; error?: string }
  | { kind: "reading" }
  | { kind: "preview"; vaults: ImportVault[]; error?: string }
  | { kind: "checking" }
  | { kind: "ask"; plans: VaultPlan[]; asking: VaultPlan[]; choices: Map<string, Choice> }
  | { kind: "running"; done: number; total: number; files: boolean }
  | { kind: "done"; summary: ImportSummary; files: boolean };

// Vaults and notes appear and change: the vault list, every tree, search and any open note are asked again.
const touched = [["vaults"], ["tree"], ["search"], ["note"], ["links"], ["backlinks"], ["history"], ["changes"]];

/**
 * Settings → Your data → Import vaults… (demo-round spec §2, "Import"). The ZIP is unpacked here; the server only
 * makes vaults and takes uploads. Each existing vault is compared with its version list before anything is written, so
 * the one question about its differing notes comes first, then the batches run.
 */
export default function ImportDialog({ onClose, importApi = serverImportApi }: { onClose(): void; importApi?: ImportApi }) {
  const id = useId();
  const queryClient = useQueryClient();
  const vaults = useVaults();
  const [view, setView] = useState<View>({ kind: "pick" });
  const [unticked, setUnticked] = useState<ReadonlySet<string>>(new Set());
  // Once versions are read, nothing must cut the import off: neither closing nor a reload into a new release.
  const busy = view.kind === "reading" || view.kind === "checking" || view.kind === "running";
  useHoldUpdates(busy);
  const close = () => {
    if (!busy) onClose();
  };
  const dialog = useDialog<HTMLDivElement>(close);

  // Each step replaces the controls the focus was on: it moves to the step's default.
  useEffect(() => {
    (dialog.current?.querySelector<HTMLElement>("[data-autofocus]") ?? dialog.current)?.focus();
  }, [view.kind, dialog]);

  // Matched as the server tells vault names apart: `work` is the vault `Work`.
  const existing = new Map((vaults.data ?? []).map((v) => [vaultKey(v.name), v]));
  const mine = (name: string) => existing.get(vaultKey(name));
  /** Why a vault of the ZIP can't be imported into its namesake, or null. */
  const blocker = (name: string) => {
    const vault = mine(name);
    return vault?.pending ? "Being re-encrypted" : vault && !vault.canWrite ? "Read only" : null;
  };
  const blocked = (name: string) => blocker(name) !== null;

  async function pick(file: File | undefined) {
    if (!file) return;
    setView({ kind: "reading" });
    let read: ImportVault[];
    try {
      read = await readImport(file, file.name);
    } catch (err) {
      setView({ kind: "pick", error: err instanceof ImportTooLarge ? err.message : "This file isn't a ZIP, or it is damaged. Choose another." });
      return;
    }
    if (read.length === 0) {
      setView({ kind: "pick", error: "This ZIP has no folders or notes to import." });
      return;
    }
    setUnticked(new Set());
    setView({ kind: "preview", vaults: read });
  }

  /** Reads the version list of each ticked vault that exists, then asks about the differing notes, if any. */
  async function check(read: ImportVault[]) {
    const chosen = read.filter((v) => !unticked.has(v.name) && !blocked(v.name));
    setView({ kind: "checking" });
    let known: { name: string; versions: Map<string, string> }[];
    try {
      known = await Promise.all(
        chosen.flatMap((v) => mine(v.name) ?? []).map(async ({ name }) => ({ name, versions: await importApi.versions(name) })),
      );
    } catch (err) {
      setView({ kind: "preview", vaults: read, error: errorText(err) });
      return;
    }
    const plans = planImport(chosen, known);
    const asking = plans.filter((p) => p.exists && p.differing.length > 0);
    if (asking.length > 0) setView({ kind: "ask", plans, asking, choices: new Map() });
    else void start(plans, new Map());
  }

  function answer(view: Extract<View, { kind: "ask" }>, choice: Choice) {
    const [current, ...rest] = view.asking;
    const choices = new Map(view.choices).set(current.vault.name, choice);
    if (rest.length > 0) setView({ ...view, asking: rest, choices });
    else void start(view.plans, choices);
  }

  async function start(plans: VaultPlan[], choices: Map<string, Choice>) {
    // With images or PDFs, the counts are of notes and files.
    const files = plans.some((p) => p.vault.files.length > 0);
    setView({ kind: "running", done: 0, total: 0, files });
    const summary = await runImport(plans, choices, importApi, (done, total) => setView({ kind: "running", done, total, files }));
    for (const queryKey of touched) void queryClient.invalidateQueries({ queryKey });
    setView({ kind: "done", summary, files });
  }

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        tabIndex={-1}
        className="flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold text-text">
          Import vaults
        </h2>

        {view.kind === "pick" && (
          <>
            <p className="text-text-body">
              A ZIP such as Kumbu²'s export: each top-level folder becomes a vault. A ZIP of one Obsidian vault becomes one vault, named after the file.
              History, trash and checkpoints aren't carried; only Markdown notes, PDFs and images (.pdf, .png, .jpg, .jpeg, .gif, .webp, up to 15 MB) are
              imported.
            </p>
            {view.error && <FormError>{view.error}</FormError>}
            <label
              className={`${secondaryButtonClass} flex cursor-pointer items-center justify-center gap-2 focus-within:outline-2 focus-within:outline-accent`}
            >
              <input type="file" accept=".zip,application/zip" aria-label="Choose a ZIP" className="sr-only" onChange={(e) => void pick(e.target.files?.[0])} />
              <FileArchive size={18} aria-hidden className="text-muted" />
              Choose a ZIP
            </label>
            <button type="button" onClick={close} className={secondaryButtonClass}>
              Cancel
            </button>
          </>
        )}

        {view.kind === "reading" && (
          <p role="status" className="text-text-body">
            Reading the ZIP …
          </p>
        )}

        {view.kind === "preview" && (
          <>
            <p className="text-text-body">Tick the vaults to import. A vault you have already is merged into; untick it to skip it.</p>
            {view.error && <FormError>{view.error}</FormError>}
            {vaults.isError && <FormError>{errorText(vaults.error)}</FormError>}
            <ul className="flex flex-col gap-2">
              {view.vaults.map((v) => (
                <li key={v.name}>
                  <label className="flex items-center gap-3">
                    <input
                      type="checkbox"
                      checked={!unticked.has(v.name) && !blocked(v.name)}
                      disabled={blocked(v.name)}
                      onChange={() =>
                        setUnticked((current) => {
                          const next = new Set(current);
                          if (!next.delete(v.name)) next.add(v.name);
                          return next;
                        })
                      }
                      className="size-4 shrink-0 accent-accent"
                    />
                    <span className="min-w-0 flex-1 break-words text-text">{v.name}</span>
                    <span className="shrink-0 text-sm text-muted">
                      {blocker(v.name) ??
                        `${count(v.notes.length, "note", "notes")}${v.files.length ? `, ${count(v.files.length, "file", "files")}` : ""}${mine(v.name) ? " · exists: merge or skip" : ""}`}
                    </span>
                  </label>
                </li>
              ))}
            </ul>
            <SkippedList skipped={view.vaults.flatMap((v) => v.skipped.map((s) => ({ path: `${v.name}/${s.path}`, reason: s.reason })))} />
            <div className="flex gap-3">
              <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              <button
                type="button"
                data-autofocus
                disabled={!vaults.data || view.vaults.every((v) => unticked.has(v.name) || blocked(v.name))}
                onClick={() => void check(view.vaults)}
                className={`${primaryButtonClass} flex-1`}
              >
                Import
              </button>
            </div>
          </>
        )}

        {view.kind === "checking" && (
          <p role="status" className="text-text-body">
            Comparing with your vaults …
          </p>
        )}

        {view.kind === "ask" && (
          <>
            <p className="text-text">
              {`${itemsText(view.asking[0].differing)} in ${view.asking[0].target} ${view.asking[0].differing.length === 1 ? "differs" : "differ"} from the ZIP.`}
            </p>
            <p className="text-text-body">Replace all with the ZIP's (the history keeps the old versions), or skip all and keep yours. Nothing is written yet.</p>
            <div className="flex flex-wrap gap-3">
              <button type="button" onClick={close} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              {/* The focus is on the choice that can't overwrite anything. */}
              <button type="button" data-autofocus onClick={() => answer(view, "skip")} className={`${secondaryButtonClass} flex-1`}>
                Skip all
              </button>
              <button type="button" onClick={() => answer(view, "replace")} className={`${primaryButtonClass} flex-1`}>
                Replace all
              </button>
            </div>
          </>
        )}

        {view.kind === "running" && (
          <div role="status" className="flex flex-col gap-2">
            <p className="text-text">{view.files ? `${view.done} of ${view.total} notes and files` : `${view.done} of ${count(view.total, "note", "notes")}`}</p>
            <div
              role="progressbar"
              aria-label="Import progress"
              aria-valuemin={0}
              aria-valuemax={view.total}
              aria-valuenow={view.done}
              className="h-2 overflow-hidden rounded-full bg-surface"
            >
              <div className="h-full bg-accent motion-safe:transition-[width]" style={{ width: `${view.total ? (view.done / view.total) * 100 : 0}%` }} />
            </div>
          </div>
        )}

        {view.kind === "done" && (
          <>
            <p role="status" className="text-text">
              {summaryText(view.summary)}
            </p>
            {view.summary.unchanged > 0 && (
              <p className="text-text-body">{`${count(view.summary.unchanged, view.files ? "note or file was" : "note was", view.files ? "notes and files were" : "notes were")} there already, as ${view.summary.unchanged === 1 ? "it is" : "they are"}.`}</p>
            )}
            {view.summary.failed.length > 0 && (
              <ul aria-label="Failed vaults" className="flex flex-col gap-1.5">
                {view.summary.failed.map((f) => (
                  <li key={f.vault} className="flex flex-col">
                    <span className="text-text">
                      {f.written > 0 ? `${f.vault} (${count(f.written, view.files ? "note or file" : "note", view.files ? "notes and files" : "notes")} written before it stopped)` : f.vault}
                    </span>
                    <span className="text-sm text-muted">{f.reason}</span>
                  </li>
                ))}
              </ul>
            )}
            <SkippedList skipped={view.summary.skipped} />
            <button type="button" data-autofocus onClick={close} className={primaryButtonClass}>
              Done
            </button>
          </>
        )}
      </div>
    </div>
  );
}
