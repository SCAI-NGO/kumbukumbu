// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Navigate, useLocation, useMatch, useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { onRefusal } from "../api/client";
import { atLeast, groupVaults, levelOf, mayChange, refusalOf, refusalSentence, useCreateUntitled, useLevel, useTree, useVaults, type VaultView } from "../api/notes";
import { FolderInput, History, TextSearch, Trash2 } from "lucide-react";
import { folderOf, noteUrl, pathFromRoute, publishableAsNote, vaultOf } from "../api/paths";
import { usePublication, usePublications, usePublishEnabled } from "../api/publications";
import { LoadBoundary, LoadFailedSheet } from "../components/AppError";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { topDialog } from "../hooks/useDialog";
import { useDesktop } from "../hooks/useDesktop";
import { useEdgeSwipe } from "../hooks/useEdgeSwipe";
import { useSidebarCollapsed } from "../hooks/useSidebarCollapsed";
import { useSyncSocket } from "../hooks/useSyncSocket";
import DiscreetPill, { DiscreetFrame, useRevealed } from "../discreet/DiscreetPill";
import { useTellOpenNote } from "../discreet/openNote";
import { useDiscreet } from "../discreet/state";
import { FindBar } from "../note/NoteTools";
import NoteView, { DeleteNoteDialog } from "../note/NoteView";
import InstallHint from "../update/InstallHint";
import UpdateBar from "../update/UpdateBar";
import { useEditor } from "../state/editor";
import DeleteFileDialog from "../attachments/DeleteFileDialog";
import { useMarking } from "./ConfirmMark";
import { copyItems, noteNames } from "./copyItems";
import Dock from "./Dock";
import type { MoveSubject } from "./MoveDialog";
import NewFolderDialog from "./NewFolderDialog";
import NewVaultDialog from "./NewVaultDialog";
import SkillsNotice from "./SkillsNotice";
import SharedBanner, { MemberNotices } from "./SharedBanner";
import StorageNotice from "./StorageNotice";
import TopBar, { sidebarToggleId } from "./TopBar";
import TreeDrawer, { sidebarId, type DeleteSubject, type VaultSheet } from "./TreeDrawer";
import { publishingItems } from "./publishingItems";
import { useCopied } from "./useCopied";

// Loaded on first use, which keeps the main bundle small: upload, rename and move are seldom used, and search has
// its own sheet over the note.
const UploadSheet = lazy(() => import("../upload/UploadSheet"));
// Its name field is focused on open, and iOS raises the keyboard only for a focus inside the tap: fetched ahead, and
// once loaded rendered directly (React.lazy would suspend on first render even then, mounting it in a later task).
let LoadedMoveDialog: (typeof import("./MoveDialog"))["default"] | null = null;
const loadMoveDialog = () =>
  import("./MoveDialog").then((m) => {
    LoadedMoveDialog = m.default;
    return m;
  });
const MoveDialog = lazy(loadMoveDialog);
const HistorySheet = lazy(() => import("../note/HistorySheet"));
const TrashSheet = lazy(() => import("./TrashSheet"));
const CheckpointsSheet = lazy(() => import("./CheckpointsSheet"));
const ActivitySheet = lazy(() => import("./ActivitySheet"));
const ArchiveSheet = lazy(() => import("./ArchiveSheet"));
const loadSearch = () => import("../search/SearchSheet");
const SearchSheet = lazy(loadSearch);
const PublishDialog = lazy(() => import("./PublishDialog"));

/** The search sheet didn't load: the dock's stand-in field lets go of the focus, so the keyboard goes down. */
function SearchLoadFailed({ onClose }: { onClose(): void }) {
  useLayoutEffect(() => {
    const active = document.activeElement;
    if (active instanceof HTMLElement && active.dataset.focusReturn) active.blur();
  }, []);
  return <LoadFailedSheet onClose={onClose} />;
}

/** `/` without a last note, or asked for ("All vaults" in the switcher): the vaults, or the way to the first one. */
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
  const { own, shared } = groupVaults(vaults.data);
  const item = (v: VaultView) => (
    <li key={v.name}>
      <button
        type="button"
        onClick={() => open(v.name)}
        className="flex min-h-12 w-full items-center justify-between gap-3 rounded-[12px] border border-line bg-surface px-4 text-left text-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        <span className="truncate font-medium">{v.name}</span>
        <span className="flex-none font-mono text-[12px] text-dim">
          {v.pending ? "Being re-encrypted" : levelOf(v) === "read" ? "Read only" : v.notes === 1 ? "1 note" : `${v.notes} notes`}
        </span>
      </button>
    </li>
  );
  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-[22px] pt-[22px] pb-7">
      <h1 className="text-[28px] leading-tight font-bold text-text">Your vaults</h1>
      <ul aria-label="Your vaults" className="mt-4 flex flex-col gap-2">
        {own.map(item)}
      </ul>
      <button type="button" onClick={onNewVault} className={`${secondaryButtonClass} mt-4`}>
        New vault
      </button>
      {shared.length > 0 && (
        <>
          <h2 className="mt-8 text-[22px] leading-tight font-bold text-text">Shared with me</h2>
          {shared.map(({ key, owner, vaults: list }) => (
            <section key={key} className="mt-4">
              {/* The owner's display name, as she typed it: quoted by the server, so it reads as a name and nothing more. */}
              <h3 className="truncate font-mono text-[12px] tracking-[0.08em] text-muted uppercase">{owner}</h3>
              <ul aria-label={`Shared by ${owner}`} className="mt-2 flex flex-col gap-2">
                {list.map(item)}
              </ul>
            </section>
          ))}
        </>
      )}
    </div>
  );
}

/** The signed-in app: top bar, the note (or the vaults), the dock, and the drawer and sheets over them. */
export default function MobileShell() {
  const match = useMatch("/notes/:vault/*");
  const path = match ? pathFromRoute(match.params.vault!, match.params["*"]!) : null;
  const { recentPaths, overlay, mode, activeVault, upload, noteTool, openNote, openUpload, setOverlay, setActiveVault } = useEditor();
  const vaults = useVaults();
  const dockLevel = useLevel(activeVault);
  const noteLevel = useLevel(path && vaultOf(path));
  const createUntitled = useCreateUntitled();
  // The note's vault's tree, which says whether its bare name is unique for Copy link. The drawer reads the same
  // query; read here it is also there before the drawer was first opened, so Copy link gives the same form either way.
  const noteTree = useTree(path ? vaultOf(path) : null);
  const noteNamesInVault = useMemo(() => noteTree.data && noteNames(noteTree.data.root), [noteTree.data]);
  const copied = useCopied("mx-3 mb-2");
  const navigate = useNavigate();
  const overview = (useLocation().state as { overview?: boolean } | null)?.overview === true;
  useSyncSocket();
  useTellOpenNote();
  const [vaultDialog, setVaultDialog] = useState(false);
  /** The folder a new folder goes into, while its dialog is open. */
  const [folderDialog, setFolderDialog] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<DeleteSubject | null>(null);
  const [moving, setMoving] = useState<MoveSubject | null>(null);
  const [history, setHistory] = useState<string | null>(null);
  const [publishing, setPublishing] = useState<string | null>(null);
  const [vaultSheet, setVaultSheet] = useState<VaultSheet | null>(null);
  // The drawer opens over the note only, never over a sheet or dialog; a swipe closes it only while it is on top.
  const swipe = useEdgeSwipe(
    overlay === "tree",
    () => useEditor.getState().overlay === "none" && !topDialog() && setOverlay("tree"),
    () => topDialog()?.dataset.drawer !== undefined && setOverlay("none"),
  );

  // From the desktop breakpoint on, the tree is docked beside the note: collapsed or expanded, never an overlay.
  const docked = useDesktop();
  const discreetShown = useRevealed();
  const [collapsed, setCollapsed] = useSidebarCollapsed();
  const [revealed, setRevealed] = useState(0);
  const toggleSidebar = useCallback(() => {
    // Hiding the tree that holds the focus would drop it to the page: it goes to the button that brings the tree back.
    if (!collapsed && document.getElementById(sidebarId)?.contains(document.activeElement)) document.getElementById(sidebarToggleId)?.focus();
    setCollapsed(!collapsed);
  }, [collapsed, setCollapsed]);

  // Docked, whatever opens the drawer (the title, the dock's Files, a vault picked or made) shows the sidebar
  // instead and takes the focus there, as the drawer would.
  useEffect(() => {
    if (!docked || overlay !== "tree") return;
    setOverlay("none");
    setCollapsed(false);
    setRevealed((n) => n + 1);
  }, [docked, overlay, setOverlay, setCollapsed]);
  useEffect(() => {
    if (!revealed) return;
    const sidebar = document.getElementById(sidebarId);
    (sidebar?.querySelector<HTMLElement>('[aria-current="page"]') ?? sidebar?.querySelector<HTMLElement>("[data-tree-row]"))?.focus();
  }, [revealed]);

  // Ctrl+\ (Cmd+\ on a Mac), as in other editors with a sidebar; the note's editor has no shortcut of its own for it.
  // `code` too, for layouts where `\` needs AltGr. Not while a dialog is open: the sidebar is behind it.
  useEffect(() => {
    if (!docked) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || e.defaultPrevented) return;
      if (e.key !== "\\" && e.code !== "Backslash") return;
      if (topDialog()) return;
      e.preventDefault();
      toggleSidebar();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [docked, toggleSidebar]);

  // The URL says which note is open; the store follows it when it changes (a link, back and forward, a reload).
  // Only then: after signing out, the store is empty while this URL is still shown for a moment.
  // Nor while the discreet vaults are being hidden: the note's URL is on its way out.
  useEffect(() => {
    if (path && path !== useEditor.getState().currentPath && !useDiscreet.getState().hiding) useEditor.getState().openNote(path);
  }, [path]);

  // A note named from its heading: the URL goes along, unless the user has gone on to another note meanwhile.
  const shownPath = useRef(path);
  shownPath.current = path;
  useEffect(() => {
    const follow = (from: string, to: string) => {
      if (shownPath.current === from) navigate(noteUrl(to), { replace: true });
    };
    useEditor.getState().setFollow(follow);
    return () => {
      if (useEditor.getState().follow === follow) useEditor.getState().setFollow(null);
    };
  }, [navigate]);
  const renaming = useEditor((s) => !!path && s.renaming.includes(path));
  const renameProblem = useEditor((s) => (s.renameProblem && s.renameProblem.path === s.currentPath ? s.renameProblem.text : null));

  // Fetched ahead, so the first tap on Search finds the sheet loaded, or nearly (see the dock's stand-in field), and
  // Rename or move finds its dialog loaded.
  useEffect(() => {
    void loadSearch().catch(() => {});
    void loadMoveDialog().catch(() => {});
  }, []);

  useEffect(() => {
    if (!activeVault && vaults.data?.length) setActiveVault(vaults.data[0].name);
  }, [activeVault, vaults.data, setActiveVault]);

  // A note the caller can't change (any more: her role was lowered) is only read. Not before the vaults are in, whose
  // levels say so: Write would be off for a moment on every note, and a note opened in Write mode would drop out of it.
  // A save of the open note the server refused counts at once, by its code, until the vault list read after it is in:
  // that list then says (collab design §9.3). Nor for a vault the list doesn't name yet: renamed in another tab, the
  // note moves to the new name before the list read again is in.
  const refused = useEditor((s) => s.refused);
  const held = refused && refused.path === path && vaults.dataUpdatedAt <= refused.at ? refusalSentence(refused.code, vaultOf(path)) : null;
  const listed = !!path && !!vaults.data?.some((v) => v.name === vaultOf(path));
  const readOnlyNote = !!held || (listed && !mayChange(noteLevel, path!));
  useEffect(() => {
    if (readOnlyNote && useEditor.getState().mode === "write") useEditor.getState().setMode("read");
  }, [readOnlyNote, mode]);
  const refusal = held ?? (path && vaults.data ? refusalOf(vaults.data.find((v) => v.name === vaultOf(path)), path) : null);

  // A write the server refused for the level (a role lowered, the owner deactivated, the vault closing): the list is
  // read again, and the write actions and Write mode follow it.
  const queryClient = useQueryClient();
  useEffect(() => onRefusal(() => void queryClient.invalidateQueries({ queryKey: ["vaults"] })), [queryClient]);

  // Only the owner publishes, only while the server does, and a published note has its bar instead (publish spec §3.11).
  const publishEnabled = usePublishEnabled();
  const noteVault = path ? vaults.data?.find((v) => v.name === vaultOf(path)) : undefined;
  const ownNote = !!noteVault && noteVault.owner == null;
  const publication = usePublication(path ?? "", ownNote && publishEnabled === true);
  const mayPublish = ownNote && publishEnabled === true && publication.data === null && publishableAsNote(noteVault, path ?? "");
  // Her marks, which keep working while publishing is switched off (spec §3.6, decision 20).
  const publications = usePublications(noteVault?.name ?? "", ownNote).data;
  const marking = useMarking(publications?.publications.map((p) => p.path));
  const publishingActions =
    path && ownNote
      ? publishingItems({ path, note: true, marks: publications?.unpublishable, mayPublish, onPublish: () => setPublishing(path), onMark: (marked) => marking.mark(path, marked) })
      : [];

  if (!path && recentPaths.length && !overview) return <Navigate to={noteUrl(recentPaths[0])} replace />;

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
  // Below `write` the dock makes nothing, and the note can't be changed; the board's definition wants `maintain`.
  const dockWrites = !!dockFolder && atLeast(dockLevel, "write");
  const noteChanges = !!path && mayChange(noteLevel, path);
  // On a phone the tree is a drawer: the frame and the pill go around and under the top bar instead (spec §3.7).
  const framed = discreetShown && !docked;
  const treeProps = { onNewNote: newNote, onNewFolder: setFolderDialog, onNewVault: () => setVaultDialog(true), onMove: setMoving, onDelete: setDeleting, onVaultSheet: setVaultSheet };

  return (
    <div data-testid="shell" {...swipe} className="relative flex h-dvh flex-col overflow-hidden bg-bg">
      {framed && <DiscreetFrame />}
      {/* No Write mode while the note is being named: a save would make it again at its old path. */}
      <TopBar path={path} writable={!readOnlyNote && !renaming} refusal={refusal} sidebar={docked ? { expanded: !collapsed, toggle: toggleSidebar } : null} />
      {framed && (
        <div className="flex-none px-3 pt-2">
          <DiscreetPill />
        </div>
      )}
      <UpdateBar />
      <MemberNotices />
      <StorageNotice />
      <SkillsNotice />
      <InstallHint />
      {/* In the tab order the docked tree comes before the note, as it is shown: top bar, tree, note, dock. */}
      <div className="flex min-h-0 flex-1">
        {docked && <TreeDrawer docked hidden={collapsed} {...treeProps} />}
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="pt-2 empty:hidden">
            <SharedBanner vault={path ? vaultOf(path) : activeVault} refusal={refusal} />
          </div>
          <main className="flex min-h-0 flex-1 flex-col">{path ? <NoteView key={path} path={path} /> : <VaultOverview onNewVault={() => setVaultDialog(true)} />}</main>
          {createUntitled.isError && (
            <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
              {errorText(createUntitled.error)}
            </p>
          )}
          {renameProblem && (
            <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
              {renameProblem}
            </p>
          )}
          {marking.error && (
            <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
              {errorText(marking.error)}
            </p>
          )}
          {copied.status}
          {path && mode === "read" && noteTool === "find" && <FindBar />}
          {(!path || mode === "read") && !(path && noteTool === "find") && (
            <Dock
              newNote={dockFolder && dockWrites ? () => newNote(dockFolder) : null}
              newFolder={dockFolder && dockWrites ? () => setFolderDialog(dockFolder) : null}
              upload={dockFolder && dockWrites ? () => openUpload(dockFolder) : null}
              noteActions={
                path
                  ? [
                      { label: "Find in note", icon: TextSearch, onSelect: () => useEditor.getState().setNoteTool("find") },
                      { label: "History", icon: History, onSelect: () => setHistory(path) },
                      ...copyItems(path, noteNamesInVault, true, copied.copy),
                      ...publishingActions,
                      ...(noteChanges
                        ? [
                            // Not while it is being named from its heading: a name given by hand must not lose that race.
                            ...(renaming ? [] : [{ label: "Rename or move", icon: FolderInput, onSelect: () => setMoving({ kind: "note", path }) }]),
                            { label: "Delete note", icon: Trash2, onSelect: () => setDeleting({ kind: "note", path }) },
                          ]
                        : []),
                    ]
                  : null
              }
            />
          )}
        </div>
      </div>

      {!docked && overlay === "tree" && <TreeDrawer {...treeProps} />}
      {overlay === "search" && (
        <LoadBoundary fallback={<SearchLoadFailed onClose={() => setOverlay("none")} />}>
          <Suspense>
            <SearchSheet />
          </Suspense>
        </LoadBoundary>
      )}
      {overlay === "upload" && upload && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setOverlay("none")} />}>
          <Suspense>
            <UploadSheet folder={upload.folder} dropped={upload.dropped} />
          </Suspense>
        </LoadBoundary>
      )}
      {moving && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setMoving(null)} />}>
          {LoadedMoveDialog ? (
            <LoadedMoveDialog subject={moving} onClose={() => setMoving(null)} />
          ) : (
            <Suspense>
              <MoveDialog subject={moving} onClose={() => setMoving(null)} />
            </Suspense>
          )}
        </LoadBoundary>
      )}
      {history && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setHistory(null)} />}>
          <Suspense>
            <HistorySheet path={history} onClose={() => setHistory(null)} />
          </Suspense>
        </LoadBoundary>
      )}
      {marking.question}
      {publishing && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setPublishing(null)} />}>
          <Suspense>
            <PublishDialog path={publishing} onClose={() => setPublishing(null)} />
          </Suspense>
        </LoadBoundary>
      )}
      {vaultSheet && (
        <LoadBoundary fallback={<LoadFailedSheet onClose={() => setVaultSheet(null)} />}>
          <Suspense>
            {vaultSheet.kind === "trash" ? (
              <TrashSheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} />
            ) : vaultSheet.kind === "activity" ? (
              <ActivitySheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} onOpenTrash={() => setVaultSheet({ kind: "trash", vault: vaultSheet.vault })} />
            ) : vaultSheet.kind === "archive" ? (
              <ArchiveSheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} />
            ) : (
              <CheckpointsSheet vault={vaultSheet.vault} onClose={() => setVaultSheet(null)} />
            )}
          </Suspense>
        </LoadBoundary>
      )}
      {deleting?.kind === "note" && <DeleteNoteDialog path={deleting.path} open={deleting.path === path} onClose={() => setDeleting(null)} />}
      {deleting?.kind === "attachment" && (
        <DeleteFileDialog path={deleting.path} version={deleting.version} onDeleted={() => setDeleting(null)} onClose={() => setDeleting(null)} />
      )}
      {folderDialog && <NewFolderDialog parent={folderDialog} onClose={() => setFolderDialog(null)} />}
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
