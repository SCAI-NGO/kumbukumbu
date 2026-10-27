// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronRight, FileText, Folder } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { errorText } from "../api/error-text";
import type { TreeFolder } from "../api/notes";
import { useCheckpointTree } from "../api/history";
import { checkpointUrl, crumbOf, pathFromRoute, titleOf } from "../api/paths";
import { timeText } from "../components/Sheet";
import CheckpointNote from "../note/CheckpointNote";
import { rows } from "../shell/TreeDrawer";

const linkClass =
  "flex min-h-11 min-w-0 flex-1 items-center gap-2 rounded-[8px] pr-2.5 text-[15px] text-text-body focus-visible:outline-2 focus-visible:outline-accent";

/**
 * A vault as a checkpoint held it, read-only by construction: this page mounts neither the shell, the editor nor
 * the autosave, and asks only for the checkpoint's own tree and notes.
 */
export default function Checkpoint() {
  const { vault = "", name = "", "*": rest } = useParams();
  const navigate = useNavigate();
  const tree = useCheckpointTree(vault, name);
  const path = rest ? pathFromRoute(vault, rest) : null;
  // Every folder open at first: a checkpoint is browsed to find a note, not tidied.
  const [closed, setClosed] = useState<Record<string, boolean>>({});

  const back = (
    <button
      type="button"
      onClick={() => navigate("/")}
      className="min-h-11 flex-none rounded-[8px] border border-on-accent/40 px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-on-accent"
    >
      Back to today
    </button>
  );

  return (
    <div className="flex h-dvh flex-col bg-bg">
      <section aria-label="Checkpoint" className="flex items-center gap-3 bg-accent px-4 py-2 text-on-accent">
        <p className="min-w-0 flex-1 text-sm font-medium [overflow-wrap:anywhere]">
          Checkpoint {name}
          {tree.data && ` · ${timeText(tree.data.checkpoint.createdAt)} · read only`}
        </p>
        {back}
      </section>

      {tree.isError && <p className="p-4 text-text">{errorText(tree.error)}</p>}
      {tree.isPending && <p className="p-4 text-sm text-muted">Loading …</p>}
      {tree.data && !tree.isError && path === null && !holdsNotes(tree.data.root) && (
        <p className="p-4 text-text">This checkpoint holds no notes.</p>
      )}
      {tree.data && !tree.isError && path === null && holdsNotes(tree.data.root) && (
        <ul className="min-h-0 flex-1 overflow-y-auto px-2 py-2">
          {rows(tree.data.root, Object.fromEntries(allFolders(tree.data.root).map((p) => [p, !closed[p]]))).map((row) => (
            <li key={row.kind === "folder" ? row.folder.path : row.path} className="flex items-center" style={{ paddingLeft: row.depth * 18 }}>
              {row.kind === "folder" ? (
                <button
                  type="button"
                  aria-expanded={row.open}
                  onClick={() => setClosed((c) => ({ ...c, [row.folder.path]: row.open }))}
                  className={`${linkClass} pl-1.5 font-medium`}
                >
                  <ChevronRight size={14} aria-hidden className={`flex-none text-dim transition-transform ${row.open ? "rotate-90" : ""}`} />
                  <Folder size={18} strokeWidth={1.8} aria-hidden className="flex-none" />
                  <span className="min-w-0 flex-1 truncate">{row.folder.name}</span>
                </button>
              ) : (
                <Link to={checkpointUrl(vault, name, row.path)} className={`${linkClass} pl-[22px]`}>
                  <FileText size={18} strokeWidth={1.8} aria-hidden className="flex-none" />
                  <span className="min-w-0 flex-1 truncate">{titleOf(row.path)}</span>
                </Link>
              )}
            </li>
          ))}
        </ul>
      )}
      {tree.data && !tree.isError && path !== null && (
        <>
          <div className="flex min-h-11 items-center gap-2 border-b border-line px-4 text-sm">
            <Link to={checkpointUrl(vault, name)} className="flex-none text-accent-text underline underline-offset-[3px]">
              All notes in this checkpoint
            </Link>
            <span className="min-w-0 truncate font-mono text-[12px] text-muted">{crumbOf(path)}</span>
          </div>
          <CheckpointNote key={`${name}/${path}`} vault={vault} name={name} createdAt={tree.data.checkpoint.createdAt} path={path} />
        </>
      )}
    </div>
  );
}

const holdsNotes = (f: TreeFolder): boolean => f.notes.length > 0 || f.folders.some(holdsNotes);

const allFolders = (f: TreeFolder): string[] => f.folders.flatMap((sub) => [sub.path, ...allFolders(sub)]);
