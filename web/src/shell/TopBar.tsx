// SPDX-License-Identifier: AGPL-3.0-or-later
import { ListTree, PanelLeft } from "lucide-react";
import { crumbOf, titleOf } from "../api/paths";
import { useEditor } from "../state/editor";
import ModeToggle from "./ModeToggle";
import { sidebarId } from "./TreeDrawer";

/** The id of the docked sidebar's toggle, which takes the focus when the shortcut hides the tree it was in. */
export const sidebarToggleId = "file-tree-toggle";

/**
 * Orientation: the tree button, where the note is and its title (a tap opens the tree), its outline, and Read / Write.
 * With `sidebar` (desktop widths), the tree button collapses and expands the docked sidebar instead of opening the drawer.
 */
export default function TopBar({ path, sidebar }: { path: string | null; sidebar: { expanded: boolean; toggle(): void } | null }) {
  const openTree = () => useEditor.getState().setOverlay("tree");
  const mode = useEditor((s) => s.mode);
  const buttonClass = "flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent";
  return (
    <header className="flex min-h-16 flex-none items-center gap-1 border-b border-line py-2.5 pr-3 pl-1.5">
      {sidebar ? (
        <button
          id={sidebarToggleId}
          type="button"
          aria-label="File tree"
          aria-expanded={sidebar.expanded}
          aria-controls={sidebarId}
          aria-keyshortcuts={"Control+\\ Meta+\\"}
          title={`${sidebar.expanded ? "Hide" : "Show"} the file tree (Ctrl+\\ or ⌘\\)`}
          onClick={sidebar.toggle}
          className={buttonClass}
        >
          <PanelLeft size={22} strokeWidth={1.8} aria-hidden />
        </button>
      ) : (
        <button type="button" aria-label="Open file tree" onClick={openTree} className={buttonClass}>
          <PanelLeft size={22} strokeWidth={1.8} aria-hidden />
        </button>
      )}
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
          className={buttonClass}
        >
          <ListTree size={21} strokeWidth={1.8} aria-hidden />
        </button>
      )}
      {path && <ModeToggle />}
    </header>
  );
}
