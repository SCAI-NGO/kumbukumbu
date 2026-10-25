// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { findFolder, useMove, useTree, type TreeFolder } from "../api/notes";
import { folderOf, noteUrl, titleOf, vaultOf } from "../api/paths";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { movedPath, useEditor } from "../state/editor";

/** What is being moved: a note (`/Work/plan.md`) or a folder (`/Work/projects`). */
export type MoveSubject = { kind: "note" | "folder"; path: string };

const allFolders = (f: TreeFolder): TreeFolder[] => [f, ...f.folders.flatMap(allFolders)];

/** `/Work/projects/deep` → `Work / projects / deep`, the vault's root as `Work (top level)`. */
const folderLabel = (path: string) => (path.split("/").length === 2 ? `${path.slice(1)} (top level)` : path.slice(1).split("/").join(" / "));

/** The server's sentence after its first `; `: what happened to the links (`rewrote 2 links in …`). */
function linksSentence(message: string) {
  const rest = message.slice(message.indexOf("; ") + 2);
  return message.includes("; ") ? rest[0].toUpperCase() + rest.slice(1) + "." : null;
}

/**
 * Renames a note or folder, or moves it to another folder of its vault (`POST /api/move`). The server rewrites
 * every link the move would break, in the same batch, so links keep leading to it; the dialog says so before,
 * and names the notes it changed after.
 */
export default function MoveDialog({ subject, onClose }: { subject: MoveSubject; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const tree = useTree(vaultOf(subject.path));
  const move = useMove();
  const navigate = useNavigate();
  const isNote = subject.kind === "note";
  const [name, setName] = useState(isNote ? titleOf(subject.path) : subject.path.slice(subject.path.lastIndexOf("/") + 1));
  const [parent, setParent] = useState(folderOf(subject.path));
  const [problem, setProblem] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  // A folder can't go into itself or below itself.
  const folders = tree.data ? allFolders(tree.data.root).filter((f) => isNote || (f.path !== subject.path && !f.path.startsWith(`${subject.path}/`))) : [];
  const fileName = isNote && !/\.md$/i.test(name.trim()) ? `${name.trim()}.md` : name.trim();
  const target = `${parent}/${fileName}`;
  const unchanged = target === subject.path;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setProblem(null);
    if (!name.trim() || name.includes("/")) return setProblem("A name can't be empty or contain “/”.");
    const there = tree.data && findFolder(tree.data.root, parent);
    // Moving onto a folder would put it inside that folder: the server's `mv`, not what this dialog says.
    if (there?.folders.some((f) => f.path.toLowerCase() === target.toLowerCase())) return setProblem(`There is a folder named “${fileName}” there already.`);
    if (there?.notes.some((n) => n.path.toLowerCase() === target.toLowerCase() && n.path !== subject.path)) return setProblem(`There is a note named “${fileName}” there already.`);

    // The open note's pending text is saved first: once moved, a save to the old path would make a new note there.
    const save = useEditor.getState().beforeRead;
    if (save && !(await save())) return setProblem("The open note has changes that can't be saved yet. Resolve them first.");

    const from = isNote ? subject.path : `${subject.path}/`;
    move.mutate(
      { from, to: target },
      {
        onSuccess: (result) => {
          const { currentPath } = useEditor.getState();
          useEditor.getState().moved(from, target);
          if (currentPath && movedPath(currentPath, from, target) !== currentPath) navigate(noteUrl(movedPath(currentPath, from, target)), { replace: true });
          setDone(linksSentence(result.message) ?? "Moved.");
        },
      },
    );
  };

  const what = isNote ? "note" : "folder";
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
          {done ? `${isNote ? "Note" : "Folder"} moved` : `Rename or move ${isNote ? titleOf(subject.path) : name}`}
        </h2>
        {done ? (
          <>
            <p role="status" className="text-text-body">{done}</p>
            <button type="button" data-autofocus onClick={onClose} className={primaryButtonClass}>
              Done
            </button>
          </>
        ) : (
          <>
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
                className={inputClass}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-folder`} className={labelClass}>
                Folder
              </label>
              <select id={`${id}-folder`} value={parent} onChange={(e) => setParent(e.target.value)} className={inputClass}>
                {(folders.length ? folders.map((f) => f.path) : [parent]).map((p) => (
                  <option key={p} value={p}>
                    {folderLabel(p)}
                  </option>
                ))}
              </select>
            </div>
            <p className="text-sm text-muted">
              Links to this {what} in other notes{isNote ? "" : " and to the notes in it"} are updated to the new place, so they keep working. It stays in this vault.
            </p>
            {problem && <FormError>{problem}</FormError>}
            {move.isError && <FormError>{errorText(move.error)}</FormError>}
            <div className="flex gap-2.5">
              <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
                Cancel
              </button>
              <button type="submit" disabled={move.isPending || unchanged || !name.trim()} className={`${primaryButtonClass} flex-1`}>
                Move
              </button>
            </div>
          </>
        )}
      </form>
    </div>
  );
}
