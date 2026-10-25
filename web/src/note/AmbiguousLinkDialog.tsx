// SPDX-License-Identifier: AGPL-3.0-or-later
import { createPortal } from "react-dom";
import { Link } from "react-router-dom";
import { folderOf, noteUrl, titleOf } from "../api/paths";
import { secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/** The notes a link could mean, each with its folder to tell them apart; following one closes the dialog. */
export default function AmbiguousLinkDialog({ target, candidates, onClose }: { target: string; candidates: string[]; onClose(): void }) {
  const dialog = useDialog<HTMLDivElement>(onClose);
  // Not inside the note's text: a dialog there would sit in a paragraph.
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="ambiguous-link-title"
        className="flex max-h-full w-full max-w-sm flex-col gap-3 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id="ambiguous-link-title" className="text-lg font-bold break-words text-text">
          Several notes match “{target}”
        </h2>
        <ul className="-mx-2 min-h-0 overflow-y-auto">
          {candidates.map((path) => (
            <li key={path}>
              <Link
                to={noteUrl(path)}
                onClick={onClose}
                className="flex min-h-11 min-w-0 flex-col justify-center rounded-[8px] px-2 py-1 focus-visible:outline-2 focus-visible:outline-accent"
              >
                <span className="truncate text-accent-text">{titleOf(path)}</span>
                <span className="font-mono text-[12px] [overflow-wrap:anywhere] text-muted">{folderOf(path).slice(1).split("/").join(" / ")}</span>
              </Link>
            </li>
          ))}
        </ul>
        <button type="button" onClick={onClose} className={secondaryButtonClass}>
          Cancel
        </button>
      </div>
    </div>,
    document.body,
  );
}
