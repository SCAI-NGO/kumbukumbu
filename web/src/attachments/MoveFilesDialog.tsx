// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { findFolder, useMove, useTree, type TreeFolder } from "../api/notes";
import { folderOf, titleOf, vaultOf } from "../api/paths";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

const allFolders = (f: TreeFolder): TreeFolder[] => [f, ...f.folders.flatMap(allFolders)];

/** `/Work/projects/deep` → `Work / projects / deep`, the vault's root as `Work (top level)`, as the move dialog says it. */
const folderLabel = (path: string) => (path.split("/").length === 2 ? `${path.slice(1)} (top level)` : path.slice(1).split("/").join(" / "));

/**
 * Moves several images and PDFs of one vault into a folder of it (`POST /api/move`, one after the other), each
 * keeping its name; the server rewrites the links to each, as for one. A name the folder has already stops it before
 * anything moves; a refusal on the way stops it there, naming the file and saying how many moved, and a second try
 * moves only those left. `onMoved` hears the paths that moved (their old ones).
 */
export default function MoveFilesDialog({ paths, onClose, onMoved }: { paths: string[]; onClose(): void; onMoved(moved: string[]): void }) {
  const id = useId();
  const tree = useTree(vaultOf(paths[0]));
  const move = useMove();
  const [parent, setParent] = useState(() => folderOf(paths[0]));
  // What is still to move: after a refusal, the files that didn't go yet.
  const [remaining, setRemaining] = useState(paths);
  const [movedSoFar, setMovedSoFar] = useState(0);
  const [problem, setProblem] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  // Not closed while files are moving: what moved and what didn't would be lost.
  const dialog = useDialog<HTMLFormElement>(() => !pending && onClose());
  const [done, setDone] = useState<string | null>(null);
  const folders = tree.data ? allFolders(tree.data.root).map((f) => f.path) : [parent];
  const moving = remaining.filter((p) => folderOf(p) !== parent);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setProblem(null);
    const there = tree.data && findFolder(tree.data.root, parent);
    const taken = new Set([...(there?.notes ?? []), ...(there?.folders ?? [])].map((n) => n.name.toLowerCase()));
    const clash = moving.map(titleOf).filter((name) => taken.has(name.toLowerCase()));
    const twice = moving.map(titleOf).filter((name, i, all) => all.findIndex((n) => n.toLowerCase() === name.toLowerCase()) !== i);
    if (clash.length + twice.length > 0) return setProblem(`The folder can't take two files named ${[...new Set([...clash, ...twice])].map((n) => `“${n}”`).join(", ")}.`);

    setPending(true);
    const moved: string[] = [];
    try {
      for (const from of moving) {
        await move.mutateAsync({ from, to: `${parent}/${titleOf(from)}` });
        moved.push(from);
      }
      const all = movedSoFar + moved.length;
      setDone(all === 1 ? "Moved 1 file." : `Moved ${all} files.`);
    } catch (error) {
      const failed = moving[moved.length];
      setProblem(`${moved.length > 0 ? `Moved ${moved.length} of ${moving.length}. ` : ""}${titleOf(failed)} didn't move: ${errorText(error)}`);
    } finally {
      setPending(false);
      setMovedSoFar((n) => n + moved.length);
      setRemaining((r) => r.filter((p) => !moved.includes(p)));
      if (moved.length > 0) onMoved(moved);
    }
  };

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={(e) => void submit(e)}
        className="k-sheet flex w-full max-w-md flex-col gap-4 rounded-[16px] border border-line bg-surface p-[18px] pb-[max(18px,env(safe-area-inset-bottom))]"
      >
        <h2 id={`${id}-title`} className="text-[20px] font-bold break-words text-text">
          {done ? "Files moved" : `Move ${remaining.length} ${remaining.length === 1 ? "file" : "files"}`}
        </h2>
        {done ? (
          <>
            <p role="status" className="text-text-body">
              {done}
            </p>
            <button type="button" data-autofocus onClick={onClose} className={primaryButtonClass}>
              Done
            </button>
          </>
        ) : (
          <>
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-folder`} className={labelClass}>
                Folder
              </label>
              <select id={`${id}-folder`} data-autofocus value={parent} onChange={(e) => setParent(e.target.value)} className={inputClass}>
                {folders.map((p) => (
                  <option key={p} value={p}>
                    {folderLabel(p)}
                  </option>
                ))}
              </select>
            </div>
            <p className="text-sm text-muted">Each keeps its name. Links to them in notes are updated to the new place, so they keep working.</p>
            {problem && <FormError>{problem}</FormError>}
            <div className="flex gap-2.5">
              <button type="button" disabled={pending} onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              <button type="submit" disabled={pending || moving.length === 0} className={`${primaryButtonClass} flex-1`}>
                Move
              </button>
            </div>
          </>
        )}
      </form>
    </div>
  );
}
