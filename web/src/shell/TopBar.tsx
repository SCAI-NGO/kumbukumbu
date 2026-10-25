// SPDX-License-Identifier: AGPL-3.0-or-later
import { ListTree, PanelLeft } from "lucide-react";
import { crumbOf, titleOf } from "../api/paths";
import { useEditor } from "../state/editor";
import ModeToggle from "./ModeToggle";

/** Orientation: the tree button, where the note is and its title (a tap opens the tree), its outline, and Read / Write. */
export default function TopBar({ path }: { path: string | null }) {
  const openTree = () => useEditor.getState().setOverlay("tree");
  const mode = useEditor((s) => s.mode);
  return (
    <header className="flex min-h-16 flex-none items-center gap-1 border-b border-line py-2.5 pr-3 pl-1.5">
      <button
        type="button"
        aria-label="Open file tree"
        onClick={openTree}
        className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
      >
        <PanelLeft size={22} strokeWidth={1.8} aria-hidden />
      </button>
      <button
        type="button"
        aria-label={path ? "Show this note in the file tree" : "Show the file tree"}
        onClick={openTree}
        className="flex min-h-11 min-w-0 flex-1 flex-col items-start justify-center text-left focus-visible:outline-2 focus-visible:outline-accent"
      >
        <span className="max-w-full truncate font-mono text-[11px] text-dim">{path ? crumbOf(path) : "Kumbu²"}</span>
        <span className="max-w-full truncate font-medium text-text">{path ? titleOf(path) : "Vaults"}</span>
      </button>
      {path && mode === "read" && (
        <button
          type="button"
          aria-label="Outline"
          aria-haspopup="dialog"
          onClick={() => useEditor.getState().setNoteTool("outline")}
          className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
        >
          <ListTree size={21} strokeWidth={1.8} aria-hidden />
        </button>
      )}
      {path && <ModeToggle />}
    </header>
  );
}
