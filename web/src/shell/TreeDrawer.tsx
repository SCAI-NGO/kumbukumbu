// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { ChevronRight, Ellipsis, FilePlus, FileText, Folder, KeyRound, LogOut, Settings, Shield } from "lucide-react";
import { Link, useNavigate } from "react-router-dom";
import { api } from "../api/client";
import { errorText } from "../api/error-text";
import { useTree, type TreeFolder } from "../api/notes";
import { noteUrl, titleOf } from "../api/paths";
import { Menu, type MenuItem } from "../components/Menu";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
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

/** The menu of a folder (the vault's root too); later steps add to the list (Upload here). */
function FolderMenu({ folder, onNewNote }: { folder: TreeFolder; onNewNote(folder: string): void }) {
  const items: MenuItem[] = [{ label: "New note here", icon: FilePlus, onSelect: () => onNewNote(folder.path) }];
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

  const signOut = async () => {
    await api.DELETE("/api/session");
    useEditor.getState().reset();
    useSession.getState().reset();
    navigate("/signin", { replace: true });
    queryClient.clear();
  };

  const link = "flex min-h-11 items-center gap-1.5 rounded-[8px] px-2 text-sm text-text-body focus-visible:outline-2 focus-visible:outline-accent";
  return (
    <nav aria-label="Account" className="flex flex-wrap items-center gap-x-1">
      <Link to="/keys" className={link}>
        <KeyRound size={16} aria-hidden className="text-muted" />
        Keys
      </Link>
      <Link to="/settings" className={link}>
        <Settings size={16} aria-hidden className="text-muted" />
        Settings
      </Link>
      {(role === "admin" || role === "primary_admin") && (
        <Link to="/admin/users" className={link}>
          <Shield size={16} aria-hidden className="text-muted" />
          Admin
        </Link>
      )}
      <button type="button" onClick={signOut} className={`${link} ml-auto`}>
        <LogOut size={16} aria-hidden className="text-muted" />
        Sign out
      </button>
    </nav>
  );
}

/** The drawer from the left: the active vault's tree, then the vault switcher and the account row at the bottom. */
export default function TreeDrawer({ onNewNote, onNewVault }: { onNewNote(folder: string): void; onNewVault(): void }) {
  const { activeVault, currentPath, expandedFolders, toggleFolder, setOverlay } = useEditor();
  const tree = useTree(activeVault);
  const navigate = useNavigate();
  const close = () => setOverlay("none");

  return (
    <div className="absolute inset-0 z-20 flex" onKeyDown={(e) => e.key === "Escape" && close()}>
      <div role="dialog" aria-label="File tree" className="k-drawer flex h-full w-[318px] max-w-[85%] flex-col border-r border-line bg-surface">
        <div className="flex flex-none items-start gap-2 px-[18px] pt-[18px] pb-2.5">
          <div className="min-w-0 flex-1">
            <p className="font-mono text-[11px] tracking-[0.08em] text-dim uppercase">Vault</p>
            <h2 className="mt-0.5 truncate text-[22px] font-bold text-text">{activeVault ?? "No vault yet"}</h2>
          </div>
          {tree.data && <FolderMenu folder={tree.data.root} onNewNote={onNewNote} />}
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
                      <FolderMenu folder={row.folder} onNewNote={onNewNote} />
                    </>
                  ) : (
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
    </div>
  );
}
