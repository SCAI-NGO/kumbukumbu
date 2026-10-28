// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { ChevronRight, Download, Ellipsis, FilePlus, Flag, Folder, FolderInput, History, Images, KeyRound, Link2, LogOut, Settings, Shield, Sparkles, SquareKanban, Trash2, Upload } from "lucide-react";
import { useState, type CSSProperties, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useBoardAction } from "../api/board";
import { api } from "../api/client";
import { errorText } from "../api/error-text";
import { findFolder, useEmbeddingProviders, useTree, useVaults, type TreeFolder } from "../api/notes";
import { boardUrl, folderOf, galleryUrl, noteUrl, titleOf, viewUrl, type FileKind, type LightboxState } from "../api/paths";
import { fileKindIcon } from "../components/fileKindIcon";
import { FormError } from "../components/form";
import { Menu, type MenuItem } from "../components/Menu";
import { LeaveDialog } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";
import { sidebarStep, useSidebarWidth } from "../hooks/useSidebarWidth";
import { useTruncated } from "../hooks/useTruncated";
import { exportUrl } from "../data/exportUrl";
import ExportConfirm from "../data/ExportConfirm";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import ConnectObsidianDialog from "./ConnectObsidianDialog";
import EmbeddingsDialog from "./EmbeddingsDialog";
import type { MoveSubject } from "./MoveDialog";
import VaultSwitcher from "./VaultSwitcher";

/** The docked sidebar's id: the top bar's button controls it. */
export const sidebarId = "file-tree";

export type Row =
  | { kind: "folder"; folder: TreeFolder; depth: number; count: number; open: boolean }
  | { kind: "note"; path: string; depth: number; fileKind: FileKind };

/** What a tree entry holds, as the server says (`kind`); an entry without one is a note. */
const fileKindOfEntry = (entry: TreeFolder["notes"][number]): FileKind => (entry.kind === "image" || entry.kind === "pdf" ? entry.kind : "note");

const count = (f: TreeFolder): number => f.notes.length + f.folders.reduce((n, sub) => n + count(sub), 0);

/** The visible rows, in the server's order (as `ls`: folders first, then notes and attachments, each in text order). */
export function rows(folder: TreeFolder, expanded: Record<string, boolean>, depth = 0): Row[] {
  return [
    ...folder.folders.flatMap((f): Row[] => {
      const open = !!expanded[f.path];
      return [{ kind: "folder", folder: f, depth, count: count(f), open }, ...(open ? rows(f, expanded, depth + 1) : [])];
    }),
    ...folder.notes.map((n): Row => ({ kind: "note", path: n.path, depth, fileKind: fileKindOfEntry(n) })),
  ];
}

const rowClass = "flex min-h-11 min-w-0 flex-1 items-center gap-2 rounded-[8px] pr-2.5 text-left text-[15px] focus-visible:outline-2 focus-visible:outline-accent";
const menuButtonClass = "flex size-11 items-center justify-center rounded-[8px] text-muted focus-visible:outline-2 focus-visible:outline-accent";

/** The menu of a folder (the vault's root too, which can't be moved, with the vault's own entries in `extra`). */
function FolderMenu({ folder, onNewNote, onMove, extra = [] }: { folder: TreeFolder; onNewNote(folder: string): void; onMove(subject: MoveSubject): void; extra?: MenuItem[] }) {
  const items: MenuItem[] = [
    { label: "New note here", icon: FilePlus, onSelect: () => onNewNote(folder.path) },
    { label: "Upload here", icon: Upload, onSelect: () => useEditor.getState().openUpload(folder.path) },
    ...(folder.path.split("/").length > 2 ? [{ label: "Rename or move", icon: FolderInput, onSelect: () => onMove({ kind: "folder", path: folder.path }) }] : []),
    ...extra,
  ];
  return (
    <Menu label={`Actions for ${folder.name}`} items={items} placement="below" className={menuButtonClass}>
      <Ellipsis size={18} aria-hidden />
    </Menu>
  );
}

function FolderButton({ folder, count, open, onToggle }: { folder: TreeFolder; count: number; open: boolean; onToggle(): void }) {
  const { ref, ...truncated } = useTruncated<HTMLSpanElement>(folder.name);
  return (
    <button type="button" aria-expanded={open} onClick={onToggle} {...truncated} className={`${rowClass} pl-1.5 font-medium text-text-body`}>
      <ChevronRight size={14} aria-hidden className={`flex-none text-dim transition-transform ${open ? "rotate-90" : ""}`} />
      <Folder size={18} strokeWidth={1.8} aria-hidden className="flex-none" />
      <span ref={ref} className="min-w-0 flex-1 truncate">{folder.name}</span>
      <span className="flex-none font-mono text-[11px] text-dim">
        {count}
        <span className="sr-only">{count === 1 ? " note" : " notes"}</span>
      </span>
    </button>
  );
}

/**
 * A note's entry shows the title, an attachment's its name with the icon of its kind; when that is cut off, the path
 * is what the tooltip tells (the title alone may repeat).
 */
function NoteButton({ path, fileKind, current, onOpen }: { path: string; fileKind: FileKind; current: boolean; onOpen(): void }) {
  const { ref, ...truncated } = useTruncated<HTMLSpanElement>(path);
  const Icon = fileKindIcon[fileKind];
  return (
    <button
      type="button"
      aria-current={current ? "page" : undefined}
      onClick={onOpen}
      data-file-kind={fileKind}
      {...truncated}
      className={`${rowClass} pl-[22px] ${current ? "bg-accent/15 text-accent-text" : "text-text-body"}`}
    >
      <Icon size={18} strokeWidth={1.8} aria-hidden className="flex-none" />
      <span ref={ref} className="min-w-0 flex-1 truncate">{titleOf(path)}</span>
    </button>
  );
}

/**
 * The drag handle on the sidebar's right edge (from `md` widths on: the drawer's `md:`, and the docked sidebar). A
 * separator that is focusable is a window splitter (ARIA): arrows resize, Home and End jump to the limits, a double
 * click resets.
 */
function ResizeHandle({ sidebar, onDragging }: { sidebar: ReturnType<typeof useSidebarWidth>; onDragging(dragging: boolean): void }) {
  const { width, min, max, set, store, reset } = sidebar;
  const [drag, setDrag] = useState<{ x: number; width: number } | null>(null);

  const end = () => {
    if (!drag) return;
    setDrag(null);
    onDragging(false);
    store();
  };
  const onKeyDown = (e: KeyboardEvent) => {
    const next = { ArrowLeft: width - sidebarStep, ArrowRight: width + sidebarStep, Home: min, End: max }[e.key];
    if (next === undefined) return;
    e.preventDefault();
    set(next);
  };
  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    e.preventDefault(); // no text selection from the press
    e.currentTarget.setPointerCapture?.(e.pointerId);
    setDrag({ x: e.clientX, width });
    onDragging(true);
  };

  return (
    <div
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize the sidebar"
      aria-valuenow={width}
      aria-valuemin={min}
      aria-valuemax={max}
      tabIndex={0}
      onKeyDown={onKeyDown}
      onPointerDown={onPointerDown}
      onPointerMove={(e) => drag && set(drag.width + e.clientX - drag.x, false)}
      onPointerUp={end}
      onPointerCancel={end}
      onDoubleClick={reset}
      className={`absolute inset-y-0 -right-1 z-10 hidden w-2 cursor-col-resize touch-none select-none transition-colors hover:bg-accent/40 focus-visible:bg-accent/40 focus-visible:outline-none md:block ${drag ? "bg-accent/40" : ""}`}
    />
  );
}

function AccountRow() {
  const role = useSession((s) => s.user?.role);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  // The drawer's state outlives the page: left open, it would be open again on the way back to the notes.
  const close = () => useEditor.getState().setOverlay("none");
  const [asking, setAsking] = useState(false);
  const [failed, setFailed] = useState(false);

  /** The open editor saves first; text it can't save (a conflict, a failure) is given up only when the user says so. */
  const signOut = async (anyway = false) => {
    setAsking(false);
    setFailed(false);
    const save = useEditor.getState().beforeRead;
    // No read of the note afterwards: it would race the DELETE below, and a 401 for it would report a lost session.
    if (!anyway && save && !(await save({ reread: false }))) return setAsking(true);
    try {
      const { response } = await api.DELETE("/api/session");
      // A 401: the session had ended already, and the app is on its way to sign-in.
      if (!response.ok && response.status !== 401) return setFailed(true);
    } catch {
      return setFailed(true);
    }
    useEditor.getState().reset();
    useSession.getState().signOut();
    navigate("/signin", { replace: true });
    queryClient.clear();
  };

  const link = "flex min-h-11 items-center gap-1.5 rounded-[8px] px-2 text-sm text-text-body focus-visible:outline-2 focus-visible:outline-accent";
  return (
    <nav aria-label="Account" className="flex flex-wrap items-center gap-x-1">
      <Link to="/keys" onClick={close} className={link}>
        <KeyRound size={16} aria-hidden className="text-muted" />
        Access
      </Link>
      <Link to="/settings" onClick={close} className={link}>
        <Settings size={16} aria-hidden className="text-muted" />
        Settings
      </Link>
      {(role === "admin" || role === "primary_admin") && (
        <Link to="/admin/users" onClick={close} className={link}>
          <Shield size={16} aria-hidden className="text-muted" />
          Admin
        </Link>
      )}
      <button type="button" onClick={() => void signOut()} className={`${link} ml-auto`}>
        <LogOut size={16} aria-hidden className="text-muted" />
        Sign out
      </button>
      {failed && (
        <p role="alert" className="w-full px-2 pb-1 text-sm text-text">
          Couldn't sign out. Try again.
        </p>
      )}
      {asking && (
        <LeaveDialog
          id="sign-out-unsaved"
          title="Sign out with unsaved changes?"
          text="The open note has changes that aren't saved. Signing out now loses them."
          leaveLabel="Sign out anyway"
          onStay={() => setAsking(false)}
          onLeave={() => void signOut(true)}
        />
      )}
    </nav>
  );
}

/** What the drawer adds around the tree below the desktop breakpoint: a modal dialog with a focus trap, and the scrim. */
function DrawerFrame({ style, className, children, after }: { style: CSSProperties; className: string; children: ReactNode; after: ReactNode }) {
  const close = () => useEditor.getState().setOverlay("none");
  const dialog = useDialog<HTMLDivElement>(close);
  return (
    <div className="absolute inset-0 z-20 flex">
      <div ref={dialog} role="dialog" aria-modal="true" aria-label="File tree" data-drawer style={style} className={`k-drawer w-[318px] max-w-[85%] md:w-(--sidebar-width) md:max-w-none ${className}`}>
        {children}
      </div>
      <button type="button" aria-label="Close file tree" onClick={close} className="flex-1 bg-black/55" />
      {after}
    </div>
  );
}

/**
 * The tree from the left: the active vault's tree, then the vault switcher and the account row at the bottom. The
 * vault's menu also opens its trash, its activity and its checkpoints (`onVaultSheet`), as sheets over it. Below the
 * desktop breakpoint a drawer over the note, which a note opened from it closes; from there on (`docked`) a sidebar
 * beside the note that stays open and is no dialog, so the focus moves freely between it and the note.
 */
export default function TreeDrawer(props: {
  docked?: boolean;
  /** Docked only: collapsed, yet kept in the page, so the top bar's button has something to control. */
  hidden?: boolean;
  onNewNote(folder: string): void;
  onNewVault(): void;
  onMove(subject: MoveSubject): void;
  onVaultSheet(sheet: { kind: "trash" | "activity" | "checkpoints"; vault: string }): void;
}) {
  const { docked = false, hidden = false, onNewNote, onNewVault, onMove, onVaultSheet } = props;
  const { activeVault, currentPath, expandedFolders, toggleFolder, setOverlay } = useEditor();
  const tree = useTree(activeVault);
  const navigate = useNavigate();
  const vaults = useVaults();
  const providers = useEmbeddingProviders();
  const [choosingEmbeddings, setChoosingEmbeddings] = useState(false);
  const [connecting, setConnecting] = useState(false);
  const [exporting, setExporting] = useState(false);
  const sidebar = useSidebarWidth();
  const [resizing, setResizing] = useState(false);
  const embeddings = vaults.data?.find((v) => v.name === activeVault)?.embeddings;
  const createBoard = useBoardAction(activeVault ?? "");
  const hasBoard = !!(activeVault && tree.data && findFolder(tree.data.root, `/${activeVault}/Board`)?.notes.some((n) => n.name === "Board.md"));
  // The board is a page of its own: the drawer, left open, would be open again on the way back to the notes.
  const openBoard = (vault: string) => {
    setOverlay("none");
    navigate(boardUrl(vault));
  };
  const openGallery = (vault: string) => {
    setOverlay("none");
    navigate(galleryUrl(vault));
  };
  /** A note opens as the note; an attachment in the lightbox, which pages through its folder's attachments. */
  const openEntry = (path: string, fileKind: FileKind) => {
    if (!docked) setOverlay("none");
    if (fileKind === "note") return navigate(noteUrl(path));
    const folder = tree.data && findFolder(tree.data.root, folderOf(path));
    const paths = folder?.notes.filter((n) => fileKindOfEntry(n) !== "note").map((n) => n.path) ?? [path];
    navigate(viewUrl(path), { state: { paths } satisfies LightboxState });
  };
  // Offered only where the admin set up an external provider: otherwise there is nothing to choose.
  const vaultItems: MenuItem[] = [
    ...(activeVault
      ? [
          { label: "Trash", icon: Trash2, onSelect: () => onVaultSheet({ kind: "trash", vault: activeVault }) },
          { label: "Activity", icon: History, onSelect: () => onVaultSheet({ kind: "activity", vault: activeVault }) },
          { label: "Checkpoints", icon: Flag, onSelect: () => onVaultSheet({ kind: "checkpoints", vault: activeVault }) },
          // Made from the template at once, so no ellipsis: that would promise a dialog.
          hasBoard
            ? { label: "Board", icon: SquareKanban, onSelect: () => openBoard(activeVault) }
            : { label: "Create board", icon: SquareKanban, onSelect: () => createBoard.mutate({ kind: "create" }, { onSuccess: () => openBoard(activeVault) }) },
          { label: "Gallery", icon: Images, onSelect: () => openGallery(activeVault) },
          { label: "Connect Obsidian…", icon: Link2, onSelect: () => setConnecting(true) },
          { label: "Export as ZIP…", icon: Download, onSelect: () => setExporting(true) },
        ]
      : []),
    ...(embeddings && providers.data?.some((p) => p.external) ? [{ label: "Search by meaning…", icon: Sparkles, onSelect: () => setChoosingEmbeddings(true) }] : []),
  ];

  const style = { "--sidebar-width": `${sidebar.width}px` } as CSSProperties;
  const className = `relative flex h-full flex-col border-r border-line bg-surface ${resizing ? "select-none" : ""}`;
  const panel = (
    <>
      <div className="flex flex-none items-start gap-2 px-[18px] pt-[18px] pb-2.5">
        <div className="min-w-0 flex-1">
          <p className="font-mono text-[11px] tracking-[0.08em] text-dim uppercase">Vault</p>
          <h2 tabIndex={-1} data-autofocus className="mt-0.5 truncate text-[22px] font-bold text-text focus:outline-none">{activeVault ?? "No vault yet"}</h2>
        </div>
        {tree.data && <FolderMenu folder={tree.data.root} onNewNote={onNewNote} onMove={onMove} extra={vaultItems} />}
      </div>
      {createBoard.isError && (
        <div className="flex-none px-[18px] pb-2.5">
          <FormError>{errorText(createBoard.error)}</FormError>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-1 [scrollbar-width:none]">
        {tree.isError && <p className="px-2.5 text-sm text-muted">{errorText(tree.error)}</p>}
        {tree.data && (
          <ul>
            {rows(tree.data.root, expandedFolders).map((row) => (
              <li key={row.kind === "folder" ? row.folder.path : row.path} className="flex items-center" style={{ paddingLeft: row.depth * 18 }}>
                {row.kind === "folder" ? (
                  <>
                    <FolderButton folder={row.folder} count={row.count} open={row.open} onToggle={() => toggleFolder(row.folder.path)} />
                    <FolderMenu folder={row.folder} onNewNote={onNewNote} onMove={onMove} />
                  </>
                ) : (
                  <>
                    <NoteButton path={row.path} fileKind={row.fileKind} current={row.path === currentPath} onOpen={() => openEntry(row.path, row.fileKind)} />
                    {/* An attachment is moved from the lightbox: the note's dialog would give it a `.md`. */}
                    {row.fileKind === "note" ? (
                      <Menu
                        label={`Actions for ${titleOf(row.path)}`}
                        items={[{ label: "Rename or move", icon: FolderInput, onSelect: () => onMove({ kind: "note", path: row.path }) }]}
                        placement="below"
                        className={menuButtonClass}
                      >
                        <Ellipsis size={18} aria-hidden />
                      </Menu>
                    ) : (
                      <span aria-hidden className="size-11 flex-none" />
                    )}
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
        {tree.data && tree.data.root.folders.length + tree.data.root.notes.length === 0 && (
          <p className="px-2.5 py-2 text-sm text-muted">No notes yet. Make the first with the menu above.</p>
        )}
        {!!tree.data?.damaged && (
          <p className="px-2.5 py-2 text-sm text-muted">
            {tree.data.damaged === 1 ? "1 note doesn't open, so it isn't shown." : `${tree.data.damaged} notes don't open, so they aren't shown.`}
          </p>
        )}
      </div>

      <div className="flex flex-none flex-col gap-2.5 border-t border-line px-3 pt-3 pb-[max(20px,env(safe-area-inset-bottom))]">
        <VaultSwitcher onNewVault={onNewVault} />
        <AccountRow />
      </div>
      {/* Last in the tab order: the tree and its controls come first. */}
      <ResizeHandle sidebar={sidebar} onDragging={setResizing} />
    </>
  );
  // Outside the sidebar's box, which would otherwise hold them: they cover the whole shell, docked or not.
  const dialogs = (
    <>
      {connecting && activeVault && <ConnectObsidianDialog vault={activeVault} onClose={() => setConnecting(false)} />}
      {exporting && activeVault && (
        <ExportConfirm title={`Export ${activeVault}?`} href={exportUrl([activeVault])} onClose={() => setExporting(false)} />
      )}
      {choosingEmbeddings && activeVault && embeddings && providers.data && (
        <EmbeddingsDialog vault={activeVault} current={embeddings} providers={providers.data} onClose={() => setChoosingEmbeddings(false)} />
      )}
    </>
  );

  if (!docked) {
    return (
      <DrawerFrame style={style} className={className} after={dialogs}>
        {panel}
      </DrawerFrame>
    );
  }
  return (
    <>
      <aside id={sidebarId} aria-label="File tree" hidden={hidden} style={style} className={`w-(--sidebar-width) flex-none ${className}`}>
        {panel}
      </aside>
      {dialogs}
    </>
  );
}
