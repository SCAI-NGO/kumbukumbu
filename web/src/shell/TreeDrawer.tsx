// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { ChevronRight, Ellipsis, FilePlus, FileText, Folder, FolderInput, KeyRound, LogOut, Settings, Shield, Sparkles, Upload } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api } from "../api/client";
import { errorText } from "../api/error-text";
import { useEmbeddingProviders, useTree, useVaults, type TreeFolder } from "../api/notes";
import { noteUrl, titleOf } from "../api/paths";
import { Menu, type MenuItem } from "../components/Menu";
import { LeaveDialog } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import EmbeddingsDialog from "./EmbeddingsDialog";
import type { MoveSubject } from "./MoveDialog";
import VaultSwitcher from "./VaultSwitcher";

type Row =
  | { kind: "folder"; folder: TreeFolder; depth: number; count: number; open: boolean }
  | { kind: "note"; path: string; depth: number };

const count = (f: TreeFolder): number => f.notes.length + f.folders.reduce((n, sub) => n + count(sub), 0);

/** The visible rows, in the server's order (as `ls`: folders first, then notes, each in text order). */
function rows(folder: TreeFolder, expanded: Record<string, boolean>, depth = 0): Row[] {
  return [
    ...folder.folders.flatMap((f): Row[] => {
      const open = !!expanded[f.path];
      return [{ kind: "folder", folder: f, depth, count: count(f), open }, ...(open ? rows(f, expanded, depth + 1) : [])];
    }),
    ...folder.notes.map((n): Row => ({ kind: "note", path: n.path, depth })),
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
        Keys
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

/** The drawer from the left: the active vault's tree, then the vault switcher and the account row at the bottom. */
export default function TreeDrawer(props: { onNewNote(folder: string): void; onNewVault(): void; onMove(subject: MoveSubject): void }) {
  const { onNewNote, onNewVault, onMove } = props;
  const { activeVault, currentPath, expandedFolders, toggleFolder, setOverlay } = useEditor();
  const tree = useTree(activeVault);
  const navigate = useNavigate();
  const close = () => setOverlay("none");
  const dialog = useDialog<HTMLDivElement>(close);
  const vaults = useVaults();
  const providers = useEmbeddingProviders();
  const [choosingEmbeddings, setChoosingEmbeddings] = useState(false);
  const embeddings = vaults.data?.find((v) => v.name === activeVault)?.embeddings;
  // Offered only where the admin set up an external provider: otherwise there is nothing to choose.
  const vaultItems: MenuItem[] =
    embeddings && providers.data?.some((p) => p.external)
      ? [{ label: "Search by meaning…", icon: Sparkles, onSelect: () => setChoosingEmbeddings(true) }]
      : [];

  return (
    <div className="absolute inset-0 z-20 flex">
      <div ref={dialog} role="dialog" aria-modal="true" aria-label="File tree" data-drawer className="k-drawer flex h-full w-[318px] max-w-[85%] flex-col border-r border-line bg-surface">
        <div className="flex flex-none items-start gap-2 px-[18px] pt-[18px] pb-2.5">
          <div className="min-w-0 flex-1">
            <p className="font-mono text-[11px] tracking-[0.08em] text-dim uppercase">Vault</p>
            <h2 tabIndex={-1} data-autofocus className="mt-0.5 truncate text-[22px] font-bold text-text focus:outline-none">{activeVault ?? "No vault yet"}</h2>
          </div>
          {tree.data && <FolderMenu folder={tree.data.root} onNewNote={onNewNote} onMove={onMove} extra={vaultItems} />}
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-2 py-1 [scrollbar-width:none]">
          {tree.isError && <p className="px-2.5 text-sm text-muted">{errorText(tree.error)}</p>}
          {tree.data && (
            <ul>
              {rows(tree.data.root, expandedFolders).map((row) => (
                <li key={row.kind === "folder" ? row.folder.path : row.path} className="flex items-center" style={{ paddingLeft: row.depth * 18 }}>
                  {row.kind === "folder" ? (
                    <>
                      <button type="button" aria-expanded={row.open} onClick={() => toggleFolder(row.folder.path)} className={`${rowClass} pl-1.5 font-medium text-text-body`}>
                        <ChevronRight size={14} aria-hidden className={`flex-none text-dim transition-transform ${row.open ? "rotate-90" : ""}`} />
                        <Folder size={18} strokeWidth={1.8} aria-hidden className="flex-none" />
                        <span className="min-w-0 flex-1 truncate">{row.folder.name}</span>
                        <span className="flex-none font-mono text-[11px] text-dim">
                          {row.count}
                          <span className="sr-only">{row.count === 1 ? " note" : " notes"}</span>
                        </span>
                      </button>
                      <FolderMenu folder={row.folder} onNewNote={onNewNote} onMove={onMove} />
                    </>
                  ) : (
                    <>
                      <button
                        type="button"
                        aria-current={row.path === currentPath ? "page" : undefined}
                        onClick={() => {
                          close();
                          navigate(noteUrl(row.path));
                        }}
                        className={`${rowClass} pl-[22px] ${row.path === currentPath ? "bg-accent/15 text-accent-text" : "text-text-body"}`}
                      >
                        <FileText size={18} strokeWidth={1.8} aria-hidden className="flex-none" />
                        <span className="min-w-0 flex-1 truncate">{titleOf(row.path)}</span>
                      </button>
                      <Menu
                        label={`Actions for ${titleOf(row.path)}`}
                        items={[{ label: "Rename or move", icon: FolderInput, onSelect: () => onMove({ kind: "note", path: row.path }) }]}
                        placement="below"
                        className={menuButtonClass}
                      >
                        <Ellipsis size={18} aria-hidden />
                      </Menu>
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
      </div>
      <button type="button" aria-label="Close file tree" onClick={close} className="flex-1 bg-black/55" />
      {choosingEmbeddings && activeVault && embeddings && providers.data && (
        <EmbeddingsDialog vault={activeVault} current={embeddings} providers={providers.data} onClose={() => setChoosingEmbeddings(false)} />
      )}
    </div>
  );
}
