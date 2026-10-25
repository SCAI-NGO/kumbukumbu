// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useEffect, useState } from "react";
import { Navigate, useMatch, useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { useCreateUntitled, useVaults } from "../api/notes";
import { Trash2 } from "lucide-react";
import { folderOf, noteUrl, pathFromRoute, vaultOf } from "../api/paths";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useEdgeSwipe } from "../hooks/useEdgeSwipe";
import NoteView, { DeleteNoteDialog } from "../note/NoteView";
import SearchSheet from "../search/SearchSheet";
import { useEditor } from "../state/editor";
import Dock from "./Dock";
import NewVaultDialog from "./NewVaultDialog";
import TopBar from "./TopBar";
import TreeDrawer from "./TreeDrawer";

// Seldom used: loaded on first use, which keeps the main bundle under Vite's 500 kB warning.
const UploadSheet = lazy(() => import("../upload/UploadSheet"));

/** `/` without a last note: the vaults, or the way to the first one. */
function VaultOverview({ onNewVault }: { onNewVault(): void }) {
  const vaults = useVaults();
  const open = (vault: string) => {
    useEditor.getState().setActiveVault(vault);
    useEditor.getState().setOverlay("tree");
  };

  if (vaults.isPending) return <div className="flex-1" />;
  if (vaults.isError) return <p className="flex-1 p-[22px] text-text-body">{errorText(vaults.error)}</p>;
  if (vaults.data.length === 0) {
    return (
      <div className="flex flex-1 flex-col justify-center gap-4 px-[22px]">
        <h1 className="text-[28px] leading-tight font-bold text-text">No vault yet</h1>
        <p className="text-text-body">A vault holds your notes, and agents get their keys to it. Start with one.</p>
        <button type="button" onClick={onNewVault} className={primaryButtonClass}>
          Create your first vault
        </button>
      </div>
    );
  }
  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-[22px] pt-[22px] pb-7">
      <h1 className="text-[28px] leading-tight font-bold text-text">Your vaults</h1>
      <ul className="mt-4 flex flex-col gap-2">
        {vaults.data.map((v) => (
          <li key={v.name}>
            <button
              type="button"
              onClick={() => open(v.name)}
              className="flex min-h-12 w-full items-center justify-between gap-3 rounded-[12px] border border-line bg-surface px-4 text-left text-text focus-visible:outline-2 focus-visible:outline-accent"
            >
              <span className="truncate font-medium">{v.name}</span>
              <span className="flex-none font-mono text-[12px] text-dim">{v.notes === 1 ? "1 note" : `${v.notes} notes`}</span>
            </button>
          </li>
        ))}
      </ul>
      <button type="button" onClick={onNewVault} className={`${secondaryButtonClass} mt-4`}>
        New vault
      </button>
    </div>
  );
}

/** The signed-in app: top bar, the note (or the vaults), the dock, and the drawer and sheets over them. */
export default function MobileShell() {
  const match = useMatch("/notes/:vault/*");
  const path = match ? pathFromRoute(match.params.vault!, match.params["*"]!) : null;
  const { recentPaths, overlay, mode, activeVault, upload, openNote, openUpload, setOverlay, setActiveVault } = useEditor();
  const vaults = useVaults();
  const createUntitled = useCreateUntitled();
  const navigate = useNavigate();
  const [vaultDialog, setVaultDialog] = useState(false);
  const [deleting, setDeleting] = useState<string | null>(null);
  const swipe = useEdgeSwipe(overlay === "tree", () => setOverlay("tree"), () => setOverlay("none"));

  // The URL says which note is open; the store follows it when it changes (a link, back and forward, a reload).
  // Only then: after signing out, the store is empty while this URL is still shown for a moment.
  useEffect(() => {
    if (path && path !== useEditor.getState().currentPath) useEditor.getState().openNote(path);
  }, [path]);

  useEffect(() => {
    if (!activeVault && vaults.data?.length) setActiveVault(vaults.data[0].name);
  }, [activeVault, vaults.data, setActiveVault]);

  if (!path && recentPaths.length) return <Navigate to={noteUrl(recentPaths[0])} replace />;

  /** A new note in `folder` (`/Work/projects`), opened in Write mode. */
  const newNote = (folder: string) =>
    createUntitled.mutate(folder, {
      onSuccess: (created) => {
        openNote(created, "write");
        navigate(noteUrl(created));
      },
    });
  // From the dock: the open note's folder, or the root of the vault the drawer shows.
  const dockFolder = activeVault && (path && vaultOf(path) === activeVault ? folderOf(path) : `/${activeVault}`);

  return (
    <div data-testid="shell" {...swipe} className="relative flex h-dvh flex-col overflow-hidden bg-bg">
      <TopBar path={path} />
      <main className="flex min-h-0 flex-1 flex-col">{path ? <NoteView key={path} path={path} /> : <VaultOverview onNewVault={() => setVaultDialog(true)} />}</main>
      {createUntitled.isError && (
        <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {errorText(createUntitled.error)}
        </p>
      )}
      {(!path || mode === "read") && (
        <Dock
          newNote={dockFolder ? () => newNote(dockFolder) : null}
          upload={dockFolder ? () => openUpload(dockFolder) : null}
          noteActions={path ? [{ label: "Delete note", icon: Trash2, onSelect: () => setDeleting(path) }] : null}
        />
      )}

      {overlay === "tree" && <TreeDrawer onNewNote={newNote} onNewVault={() => setVaultDialog(true)} />}
      {overlay === "search" && <SearchSheet />}
      {overlay === "upload" && upload && (
        <Suspense>
          <UploadSheet folder={upload.folder} dropped={upload.dropped} />
        </Suspense>
      )}
      {deleting && <DeleteNoteDialog path={deleting} onClose={() => setDeleting(null)} />}
      {vaultDialog && (
        <NewVaultDialog
          onClose={() => setVaultDialog(false)}
          onCreated={(name) => {
            setVaultDialog(false);
            setActiveVault(name);
            setOverlay("tree");
          }}
        />
      )}
    </div>
  );
}
