// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, type MouseEvent, type ReactNode } from "react";
import { mayChange, refusalSentence, useLevel, useVaults } from "../api/notes";
import { titleOf, vaultOf, type NoteAt } from "../api/paths";
import { NO_SUCH_NOTE } from "../discreet/errorText";
import { useDiscreet, useGoneWithVaults } from "../discreet/state";
import NoteView from "../note/NoteView";
import { useEditor, type Side } from "../state/editor";
import NoteBar from "./NoteBar";
import { PaneSide, useOpenNote } from "./openInPane";

/**
 * Whether the note at `path` may be written here: `readOnly` once the vault list says it can't be changed, or while a
 * save the server refused for the level holds (`held`, its sentence) until the vault list read after it is in (collab
 * design §9.3); not for a vault the list doesn't name yet (renamed in another tab, the note moves to the new name before
 * the list read again is in). `changes`: Rename and Delete are offered.
 */
export function useNoteAccess(path: string | null) {
  const vaults = useVaults();
  const level = useLevel(path && vaultOf(path));
  const refused = useEditor((s) => s.refused);
  const held = path && refused && refused.path === path && vaults.dataUpdatedAt <= refused.at ? refusalSentence(refused.code, vaultOf(path)) : null;
  const listed = !!path && !!vaults.data?.some((v) => v.name === vaultOf(path));
  return { held, readOnly: !!held || (listed && !mayChange(level, path!)), changes: !!path && mayChange(level, path) };
}

/** A link's note and where in it, as the links of `MarkdownView` carry them (`data-note-path`, `data-note-at`). */
function linkOf(target: EventTarget): { path: string; at?: NoteAt } | null {
  const link = target instanceof Element ? target.closest<HTMLElement>("[data-note-path]") : null;
  if (!link?.dataset.notePath) return null;
  return { path: link.dataset.notePath, at: link.dataset.noteAt ? (JSON.parse(link.dataset.noteAt) as NoteAt) : undefined };
}

/**
 * One pane: the note bar and the note, in `side`'s pane for what is in it (its links, its mode, its editor's guard).
 * With `split`, the split view's pane: its focus marked plainly (workspace spec §3), a pointer down or the focus in it
 * focusing it without starting Write mode, and `children` (the dock and what goes with it) under it when it is focused. From 1280
 * px on (`wide`), Ctrl+Alt+click (Cmd+Alt on a Mac) on a link opens it in the right pane, the split started if need be.
 */
export default function Pane({
  side,
  path,
  split,
  hidden = false,
  wide,
  panel,
  onMove,
  onDelete,
  onSplit,
  onClose,
  children,
}: {
  side: Side;
  path: string;
  split: boolean;
  /** Out of sight below 1280 px, the split's other pane: mounted still, so a writing one keeps its editor and text. */
  hidden?: boolean;
  wide: boolean;
  panel: boolean;
  onMove(path: string): void;
  onDelete(path: string): void;
  /** Split view, in the left pane's note bar from 1280 px on: on while the split is shown. */
  onSplit: { on: boolean; toggle(): void } | null;
  onClose(): void;
  children?: ReactNode;
}) {
  const gone = useGoneWithVaults(path);
  const hiding = useDiscreet((s) => s.hiding);
  const focused = useEditor((s) => s.focused === side);
  const { readOnly, changes } = useNoteAccess(path);
  const renaming = useEditor((s) => s.renaming.includes(path));
  const openNote = useOpenNote();

  // A note that can't be changed (any more) is only read, in whichever pane writes it. Not before the vaults are in,
  // whose levels say so: Write would be off for a moment on every note.
  const writing = useEditor((s) => s.panes[side]?.path === path && s.panes[side]!.mode === "write");
  useEffect(() => {
    if (readOnly && writing) useEditor.getState().setPane(side, { path, mode: "read" });
  }, [readOnly, writing, side, path]);

  const openRight = (e: MouseEvent) => {
    if (!wide || !(e.ctrlKey || e.metaKey) || !e.altKey) return;
    const link = linkOf(e.target);
    if (!link) return;
    e.preventDefault();
    e.stopPropagation();
    openNote(link.path, { side: "right", state: link.at });
  };

  const dim = split && !focused;
  const content = gone ? (
    // Empty while the hide goes on; reached again later (a bookmark, say), said as a discreet note's 404 is.
    <div className="flex-1 px-[22px] pt-[22px]">{!hiding && <p className="text-text-body">{NO_SUCH_NOTE}</p>}</div>
  ) : (
    <>
      {(changes || split || onSplit) && (
        <NoteBar
          title={split ? titleOf(path) : null}
          dim={dim}
          onRename={changes && !renaming ? () => onMove(path) : null}
          onDelete={changes ? () => onDelete(path) : null}
          split={onSplit}
          onClose={split ? onClose : null}
        />
      )}
      <NoteView key={path} path={path} panel={panel} dim={dim} />
    </>
  );

  // The same element split or not, only its attributes change: the editor in it stays mounted as the split starts or
  // ends (MobileShell). Alone, it lays out as if it weren't there; hidden, it is the other pane below 1280 px.
  const name = side === "left" ? "Left pane" : "Right pane";
  return (
    <PaneSide.Provider value={side}>
      <div
        role={split ? "region" : undefined}
        aria-label={split ? (focused ? `${name}, focused` : name) : undefined}
        aria-current={split && focused ? "true" : undefined}
        data-pane={split ? side : undefined}
        tabIndex={split ? -1 : undefined}
        hidden={hidden}
        // A pointer down or the keyboard's focus (Tab to a link in it) focuses the pane: what opens next opens there.
        onPointerDown={split ? () => useEditor.getState().focus(side) : undefined}
        onFocusCapture={split ? () => useEditor.getState().focus(side) : undefined}
        onClickCapture={openRight}
        className={
          split
            ? `flex min-w-0 flex-1 basis-0 flex-col border-t-[3px] outline-none ${focused ? "border-t-accent" : "border-t-transparent"} ${side === "right" ? "border-l border-l-line" : ""}`
            : "contents"
        }
      >
        {content}
        {split && focused && children}
      </div>
    </PaneSide.Provider>
  );
}
