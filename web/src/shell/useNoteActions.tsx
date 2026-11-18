// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { CopyPlus, Flag, FolderInput, History, ListPlus, Star, TextSearch, Trash2 } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { mayChange, useDuplicateNote, useLevel, useNoteText, useTree, useVaults } from "../api/notes";
import { inVault, noteUrl, publishableAsNote, vaultOf } from "../api/paths";
import { usePublications, usePublishEnabled } from "../api/publications";
import { useFavourites } from "../api/favourites";
import { LoadBoundary, LoadFailedSheet } from "../components/AppError";
import type { MenuItem } from "../components/Menu";
import { useNoteEntry } from "../calendar/api";
import { calendarItems, useRemoveFromCalendar, useTaskSwitch } from "../calendar/DateDialog";
import { goneVault, useDiscreet } from "../discreet/state";
import { flaggable } from "../flags/FlagMenu";
import DuplicateNotice from "../note/DuplicateNotice";
import { parseProperties, splitFrontmatter } from "../note/properties";
import { staleNotes } from "../palette/rows";
import { useEditor } from "../state/editor";
import { useMarking } from "./ConfirmMark";
import { copyItems, noteNames } from "./copyItems";
import type { MoveSubject } from "./MoveDialog";
import type { DeleteSubject } from "./TreeDrawer";
import { publishingItems } from "./publishingItems";
import { useCopied } from "./useCopied";

const HistorySheet = lazy(() => import("../note/HistorySheet"));
const PublishDialog = lazy(() => import("./PublishDialog"));

const noteFavourite = (path: string) => ({ kind: "note", vault: vaultOf(path), path: inVault(path) });

/** What the shell shares with the tree's menus: their dialogs for moving, deleting, dating and flagging. */
export interface NoteActionsShell {
  onMove(subject: MoveSubject): void;
  onDelete(subject: DeleteSubject): void;
  onDate(path: string): void;
  onFlag(path: string): void;
}

/**
 * The open note's actions, one list for every place that offers them, with the items that fit the mode. `notices` go
 * with the shell's other messages above the dock; `dialogs` are what the items open, rendered once by the shell.
 */
export function useNoteActions(path: string | null, shell: NoteActionsShell): { items: MenuItem[]; notices: ReactNode; dialogs: ReactNode } {
  const mode = useEditor((s) => s.mode);
  const noteLevel = useLevel(path && vaultOf(path));
  const vaults = useVaults();
  // The note's vault's tree, which says whether its bare name is unique for Copy link. The drawer reads the same
  // query; read here it is also there before the drawer was first opened, so Copy link gives the same form either way.
  const noteTree = useTree(path ? vaultOf(path) : null);
  const noteNamesInVault = useMemo(() => noteTree.data && noteNames(noteTree.data.root), [noteTree.data]);
  const favourites = useFavourites();
  const copied = useCopied("mx-3 mb-2");
  const noteText = useNoteText(path);
  const renaming = useEditor((s) => !!path && s.renaming.includes(path));
  const [history, setHistory] = useState<string | null>(null);
  const [publishing, setPublishing] = useState<string | null>(null);
  const duplicating = useDuplicateNote();
  /** The copy just made, whose notice is up. */
  const [copy, setCopy] = useState<string | null>(null);
  const failedCopy = useRef<{ of?: string; reset(): void }>({ reset: () => {} });
  failedCopy.current = { of: duplicating.variables, reset: duplicating.reset };
  // A sheet, dialog or notice about a vault hidden again goes with it: its title and its answers name the note.
  useEffect(
    () =>
      useDiscreet.subscribe((s, previous) => {
        if (s.gone === previous.gone && s.names === previous.names) return;
        const gonePath = (path: string | null) => !!path && goneVault(s, vaultOf(path));
        setHistory((path) => (gonePath(path) ? null : path));
        setPublishing((path) => (gonePath(path) ? null : path));
        setCopy((path) => (gonePath(path) ? null : path));
        if (gonePath(failedCopy.current.of ?? null)) failedCopy.current.reset();
      }),
    [],
  );

  // Only the owner publishes, only while the server does, and a published note has its bar instead (publish spec §3.11).
  const publishEnabled = usePublishEnabled();
  const noteVault = path ? vaults.data?.find((v) => v.name === vaultOf(path)) : undefined;
  const ownNote = !!noteVault && noteVault.owner == null;
  // Her marks, which keep working while publishing is switched off (spec §3.6, decision 20), and what is published.
  const publications = usePublications(noteVault?.name ?? "", ownNote).data;
  // Not in Write mode: publishing, as History's restore and Add property, acts on the saved text while unsaved text is on screen.
  const mayPublish =
    mode === "read" &&
    ownNote && publishEnabled === true && publications !== undefined && !publications.publications.some((p) => p.path === path) && publishableAsNote(noteVault, path ?? "");
  const marking = useMarking(publications?.publications.map((p) => p.path));
  const publishingActions =
    path && ownNote
      ? publishingItems({ path, note: true, marks: publications?.unpublishable, mayPublish, onPublish: () => setPublishing(path), onMark: (marked) => marking.mark(path, marked) })
      : [];

  // The note's header line asks the calendar about the note shown; its menu takes the same answer, asking nothing itself.
  const dated = useNoteEntry(path, false);
  const removeDate = useRemoveFromCalendar(copied.say);
  const switchTask = useTaskSwitch(copied.say);
  const noteChanges = !!path && mayChange(noteLevel, path);
  // The note's properties show in a box with + Add property when it has keys to show; without (no frontmatter, an
  // empty one, or one that can't be read) the note's ⋯ offers it.
  const shownFrontmatter = noteText === undefined ? undefined : splitFrontmatter(noteText).frontmatter;
  const boxless = noteText !== undefined && !(shownFrontmatter != null && parseProperties(shownFrontmatter)?.length);

  // The copy is made of the text as it is shown: in Write mode what is unsaved is saved first, without ending Write
  // mode. A failed save copies nothing; the editor says why, as for any save. The original stays open.
  const navigate = useNavigate();
  const duplicate = async (path: string) => {
    duplicating.reset();
    const save = useEditor.getState().beforeRead;
    if (save && !(await save({ reread: false }))) return;
    setCopy(null);
    duplicating.mutate(path, { onSuccess: setCopy });
  };

  const items: MenuItem[] = path
    ? [
        ...(mode === "read"
          ? [
              { label: "Find in note", icon: TextSearch, onSelect: () => useEditor.getState().setNoteTool("find") },
              { label: "History", icon: History, onSelect: () => setHistory(path) },
            ]
          : []),
        ...copyItems(path, noteNamesInVault, true, copied.copy),
        // Once the list is read: a change before would replace it. The change lets go of the vault's favourites whose note is gone.
        // None for an archived note: the server takes none.
        ...(favourites.ready && !inVault(path).startsWith("_archive/")
          ? [
              {
                label: favourites.has(noteFavourite(path)) ? "Remove from favourites" : "Add to favourites",
                icon: Star,
                onSelect: () => favourites.toggle(noteFavourite(path), noteTree.isSuccess ? staleNotes(favourites.list, new Map([[vaultOf(path), { tree: noteTree.data, at: noteTree.dataUpdatedAt }]])) : []),
              },
            ]
          : []),
        ...publishingActions,
        ...(noteChanges
          ? [
              // Not while it is being named from its heading: a name given by hand must not lose that race. No copy of
              // an archived note: it would be archived too.
              ...(renaming || inVault(path).startsWith("_archive/") ? [] : [{ label: "Duplicate", icon: CopyPlus, onSelect: () => void duplicate(path) }]),
              ...(renaming ? [] : [{ label: "Rename or move", icon: FolderInput, onSelect: () => shell.onMove({ kind: "note", path }) }]),
              { label: "Delete note", icon: Trash2, onSelect: () => shell.onDelete({ kind: "note", path }) },
              ...(flaggable(path) ? [{ label: "Flag", icon: Flag, onSelect: () => shell.onFlag(path) }] : []),
              ...calendarItems(path, dated, shell.onDate, removeDate, switchTask),
              ...(boxless && mode === "read" ? [{ label: "Add property…", icon: ListPlus, onSelect: () => useEditor.getState().setNoteTool("addProperty") }] : []),
            ]
          : []),
      ]
    : [];

  const notices = (
    <>
      {favourites.error && (
        <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {favourites.error}
        </p>
      )}
      {marking.error && (
        <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {errorText(marking.error)}
        </p>
      )}
      {duplicating.isError && (
        <p role="alert" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {errorText(duplicating.error)}
        </p>
      )}
      {copy && (
        <DuplicateNotice
          key={copy}
          name={copy.slice(copy.lastIndexOf("/") + 1)}
          onOpen={() => {
            setCopy(null);
            navigate(noteUrl(copy));
          }}
          onClose={() => setCopy(null)}
        />
      )}
      {copied.status}
    </>
  );

  const dialogs = (
    <>
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
    </>
  );

  return { items, notices, dialogs };
}
