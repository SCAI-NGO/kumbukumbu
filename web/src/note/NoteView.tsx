// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/user-client/src/routes/app/projects/file.tsx)
import { lazy, Suspense, useCallback, useDeferredValue, useEffect, useLayoutEffect, useMemo, useRef, useState, type DragEvent } from "react";
import { useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { collectFromDrop } from "../upload/collectFiles";
import { useBacklinks, useCreateNote, useDeleteNote, useLinks, useNote, useSaveNote } from "../api/notes";
import { crumbOf, folderOf, noteUrl, titleOf } from "../api/paths";
import { LoadBoundary, LoadFailedSheet } from "../components/AppError";
import { secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import AgentBanner, { useAgentChange, useAgentHighlight } from "./AgentBanner";
import { storedAutoRefresh } from "./autoRefresh";
import { BarButton, ChangeText, ElsewhereBar, useChangedElsewhere, useTimeout, writerOf } from "./ChangedElsewhere";
import { changedLines } from "./lineDiff";
import BacklinksList from "./BacklinksList";
import MarkdownView, { type Highlight } from "./MarkdownView";
import { OutlineSheet } from "./NoteTools";
import { outline } from "./outline";
import { parseProperties, splitFrontmatter } from "./properties";
import { taskToggle } from "./taskToggle";

// Write mode (the editor, its toolbar and the [[ list) is loaded apart from reading; fetched ahead once a note is
// shown, so switching to Write rarely waits.
// React.lazy suspends on first render even when the module is in already, which would mount the editor in a later
// task, outside the tap on Write (iOS then raises no keyboard): once loaded, the editor is rendered directly.
let LoadedEditor: (typeof import("./MarkdownEditor"))["default"] | null = null;
const loadEditor = () =>
  import("./MarkdownEditor").then((m) => {
    LoadedEditor = m.default;
    return m;
  });
const LazyEditor = lazy(loadEditor);

/** Write mode didn't load: the Write button's stand-in field lets go of the focus, so the keyboard goes down. */
function EditorLoadFailed() {
  useLayoutEffect(() => {
    const active = document.activeElement;
    if (active instanceof HTMLElement && active.dataset.standIn !== undefined) active.blur();
  }, []);
  return <LoadFailedSheet onClose={() => useEditor.getState().setMode("read")} />;
}

const CONFLICT_NOTICE = "Changed elsewhere — showing the newer version; tap again.";

/**
 * Whether the body opens with its own H1, ATX (`# Title`) or setext (`Title` over `===`). The frontmatter's lines
 * are empty in it; leading blank lines go, but not indentation: four spaces make a code block, not a heading.
 */
const startsWithH1 = (body: string) => {
  const first = body.replace(/^(?:[ \t]*\r?\n)+/, "");
  return /^ {0,3}#(?:[ \t]|$)/.test(first.split("\n", 1)[0]) || /^ {0,3}\S.*\r?\n {0,3}=+[ \t]*\r?$/.test(first.split("\n").slice(0, 2).join("\n"));
};

/** The notice under the title: a write that lost to a newer version, or one that failed. */
type Notice = { kind: "conflict" } | { kind: "error"; text: string };

/**
 * Read mode (MOBILE_EDITOR §4): the agent banner, the rendered note, and the notes linking to it. Write mode: the
 * editor with its toolbar, in place of the dock.
 */
export default function NoteView({ path }: { path: string }) {
  const note = useNote(path);
  const mode = useEditor((s) => s.mode);
  // Every save is a new version; the links are only asked for once they are shown again.
  const links = useLinks(path, mode === "read" ? note.data?.version : undefined);
  const save = useSaveNote(path);
  const createNote = useCreateNote();
  const openNote = useEditor((s) => s.openNote);
  const navigate = useNavigate();
  const [notice, setNotice] = useState<Notice | null>(null);
  const [dropping, setDropping] = useState(false);
  const parts = useMemo(() => splitFrontmatter(note.data?.content ?? ""), [note.data?.content]);
  const change = useAgentChange(path);
  const agentHighlight = useAgentHighlight(path, change, note.data?.content);
  const elsewhere = useChangedElsewhere(path, note.data);
  const username = useSession((s) => s.user?.username);
  // What a refresh brought, tinted as the agent banner tints: for the version it brought only.
  const [refreshed, setRefreshed] = useState<(Highlight & { version: string }) | null>(null);
  const [updatedBy, setUpdatedBy] = useState<string | null>(null);
  useTimeout(updatedBy !== null, 6_000, () => setUpdatedBy(null));
  const highlight = refreshed && refreshed.version === note.data?.version ? refreshed : agentHighlight;
  // The frontmatter's lines are 2 to its closing line (`parts.frontmatter` has one line fewer than that).
  const frontmatterChanged =
    !!highlight && parts.frontmatter !== null && (highlight.lines === "all" || [...highlight.lines].some((l) => l <= parts.frontmatter!.split("\n").length + 2));

  const noteTool = useEditor((s) => s.noteTool);
  const find = useEditor((s) => s.find);
  const headings = useMemo(() => (noteTool === "outline" ? outline(parts.body) : []), [noteTool, parts.body]);
  // Typing stays quick in a long note: the marks follow the query when React has time.
  const query = useDeferredValue(find.query);
  const finding = noteTool === "find" && query !== "" ? { query, current: find.index } : null;

  // A search result names a section: scroll to the last heading at or above its first line, once the note is shown.
  const scrollTarget = useEditor((s) => s.scrollTarget);
  const article = useRef<HTMLElement>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const keepScroll = useRef<number | null>(null);
  const refreshing = useRef(false);

  /** Takes the version written elsewhere: the reader stays where they were, and what changed is tinted. */
  const refresh = useCallback(
    async (writer: string, auto: boolean) => {
      if (refreshing.current || !note.data) return;
      refreshing.current = true;
      const before = note.data.content;
      const top = scroller.current?.scrollTop ?? 0;
      const fresh = await note.refetch();
      refreshing.current = false;
      if (!fresh.data) return;
      keepScroll.current = top;
      setRefreshed({ lines: changedLines(before, fresh.data.content), writer, version: fresh.data.version });
      setUpdatedBy(auto ? writer : null);
    },
    [note],
  );
  useLayoutEffect(() => {
    if (keepScroll.current === null || !scroller.current) return;
    scroller.current.scrollTop = keepScroll.current;
    keepScroll.current = null;
  }, [note.data?.content]);

  // With "Refresh Read mode automatically" on, Read mode takes a version written elsewhere by itself. Write mode never does.
  const autoRefresh = mode === "read" && storedAutoRefresh();
  const newer = elsewhere.change;
  useEffect(() => {
    if (autoRefresh && newer) void refresh(writerOf(newer.lastWriter), true);
  }, [autoRefresh, newer, refresh]);
  useEffect(() => {
    if (!scrollTarget || scrollTarget.path !== path || mode !== "read" || !note.data || !article.current) return;
    const headings = [...article.current.querySelectorAll<HTMLElement>("[data-line]")].filter((h) => Number(h.dataset.line) <= scrollTarget.line);
    headings.at(-1)?.scrollIntoView({ block: "start" });
    useEditor.getState().setScrollTarget(null);
  }, [scrollTarget, path, mode, note.data]);

  // Find in the note: the note says how many matches it marked, and the current one comes into view.
  useEffect(() => {
    if (noteTool !== "find" || mode !== "read" || !article.current) return;
    const count = article.current.querySelectorAll<HTMLElement>("mark[data-find]").length;
    const { find } = useEditor.getState();
    // Fewer matches than before (an edit, a longer query): the current one is the last that is left.
    if (count !== find.count || (count > 0 && find.index >= count)) useEditor.getState().setFind({ count, index: Math.min(find.index, Math.max(0, count - 1)) });
    const current = article.current.querySelector<HTMLElement>("mark[data-current]");
    // A match in a folded callout is shown by unfolding it.
    current?.closest("details")?.setAttribute("open", "");
    current?.scrollIntoView({ block: "center" });
  }, [noteTool, mode, finding?.query, finding?.current, note.data?.content]);

  const jump = (line: number) => {
    useEditor.getState().setNoteTool(null);
    article.current?.querySelector<HTMLElement>(`[data-line="${line}"]`)?.scrollIntoView({ block: "start" });
  };

  useEffect(() => {
    void loadEditor().catch(() => {});
  }, []);

  const toggleTask = useCallback(
    (line: number) => {
      if (!note.data) return;
      const content = taskToggle(note.data.content, line);
      if (content === null) return;
      setNotice(null);
      save.mutate(
        { content, version: note.data.version },
        {
          // On a conflict the cache already holds the newer version; the tap is not repeated on top of it.
          onSuccess: (result) => setNotice("conflict" in result ? { kind: "conflict" } : null),
          onError: (err) => setNotice({ kind: "error", text: errorText(err) }),
        },
      );
    },
    [note.data, save],
  );

  const createMissing = useCallback(
    (target: string) => {
      const created = `${folderOf(path)}/${target.endsWith(".md") ? target : `${target}.md`}`;
      createNote.mutate(created, {
        onSuccess: () => {
          openNote(created, "write");
          navigate(noteUrl(created));
        },
        onError: (err) => setNotice({ kind: "error", text: errorText(err) }),
      });
    },
    [path, createNote, openNote, navigate],
  );

  if (mode === "write") {
    if (note.data) {
      return (
        <LoadBoundary fallback={<EditorLoadFailed />}>
          {LoadedEditor ? (
            <LoadedEditor path={path} note={note.data} elsewhere={elsewhere} />
          ) : (
            <Suspense fallback={<div className="flex-1" />}>
              <LazyEditor path={path} note={note.data} elsewhere={elsewhere} />
            </Suspense>
          )}
        </LoadBoundary>
      );
    }
    return <div className="flex-1 px-[22px] pt-[22px]">{note.isError && <p className="text-text-body">{errorText(note.error)}</p>}</div>;
  }

  // Files dropped on the note go into its folder, by way of the upload sheet (which asks before sending).
  const dropTarget = {
    onDragOver: (e: DragEvent) => {
      if (!e.dataTransfer.types.includes("Files")) return;
      e.preventDefault();
      setDropping(true);
    },
    onDragLeave: (e: DragEvent) => {
      if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDropping(false);
    },
    onDrop: (e: DragEvent) => {
      if (!e.dataTransfer.types.includes("Files")) return;
      e.preventDefault();
      setDropping(false);
      void collectFromDrop(e.dataTransfer.items).then((picked) => useEditor.getState().openUpload(folderOf(path), picked));
    },
  };

  return (
    <>
      <AgentBanner change={change} />
      {elsewhere.gone ? (
        <ElsewhereBar>This note was moved or removed elsewhere.</ElsewhereBar>
      ) : newer && !autoRefresh ? (
        <ElsewhereBar actions={<BarButton onClick={() => void refresh(writerOf(newer.lastWriter), false)}>Refresh</BarButton>}>
          <ChangeText change={newer} />
        </ElsewhereBar>
      ) : (
        updatedBy && (
          <p role="status" aria-label="Updated" className="flex min-h-11 flex-none items-center border-b border-line bg-surface px-4 text-[13px] text-muted">
            {updatedBy === username ? "Updated: you changed this note elsewhere" : `Updated by ${updatedBy}`}
          </p>
        )
      )}
      <div ref={scroller} {...dropTarget} className={`min-h-0 flex-1 overflow-y-auto px-[22px] pt-[22px] pb-7 ${dropping ? "outline-2 -outline-offset-4 outline-dashed outline-accent" : ""}`}>
        {/* A note with its own H1 would show two titles; the page keeps exactly one. */}
        {!(note.data && startsWithH1(parts.body)) && <h1 className="text-[28px] leading-tight font-bold text-text">{titleOf(path)}</h1>}
        {notice && (
          <p
            role={notice.kind === "conflict" ? "status" : "alert"}
            className="mt-3 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text"
          >
            {notice.kind === "conflict" ? CONFLICT_NOTICE : notice.text}
          </p>
        )}
        {note.isError && <p className="mt-4 text-text-body">{errorText(note.error)}</p>}
        {note.data && (
          <article ref={article} className="mt-4">
            {parts.frontmatter !== null && <Properties yaml={parts.frontmatter} changed={frontmatterChanged ? highlight!.writer : null} />}
            <MarkdownView
              source={parts.body}
              links={links.data}
              tasksDisabled={save.isPending}
              onToggleTask={toggleTask}
              onCreateMissing={createMissing}
              highlight={highlight}
              find={finding}
              path={path}
            />
          </article>
        )}
        {note.data && <BacklinksList path={path} />}
      </div>
      {noteTool === "outline" && note.data && <OutlineSheet headings={headings} onJump={jump} onClose={() => useEditor.getState().setNoteTool(null)} />}
    </>
  );
}

/**
 * The front matter as a compact block of properties (key, value; lists as chips), as Obsidian shows it. YAML this
 * can't take apart faithfully is shown as written. Write mode keeps it in the source, where it is edited.
 */
function Properties({ yaml, changed }: { yaml: string; changed: string | null }) {
  const props = useMemo(() => parseProperties(yaml), [yaml]);
  const tint = changed ? { "data-changed": true, "data-writer": changed } : {};
  if (!props) {
    return (
      <pre
        aria-label="Properties"
        {...tint}
        className="mb-4 overflow-x-auto rounded-[10px] border border-line bg-surface px-3.5 py-2.5 font-mono text-[12.5px] leading-[1.6] whitespace-pre-wrap text-muted"
      >
        {yaml}
      </pre>
    );
  }
  if (props.length === 0) return null;
  return (
    <dl
      aria-label="Properties"
      {...tint}
      className="mb-4 grid grid-cols-[minmax(0,auto)_minmax(0,1fr)] gap-x-3 gap-y-1 rounded-[10px] border border-line bg-surface px-3.5 py-2.5 text-[14px] leading-[1.5]"
    >
      {props.map(({ key, value }, i) => (
        <div key={`${i}${key}`} className="contents">
          <dt className="truncate font-mono text-[12px] leading-[21px] text-muted">{key}</dt>
          <dd className="min-w-0 break-words text-text-body">
            {Array.isArray(value) ? (
              <ul className="flex flex-wrap gap-1">
                {value.map((v, j) => (
                  <li key={j} className="rounded-full bg-surface-2 px-2 text-[13px] text-text-body">
                    {v}
                  </li>
                ))}
              </ul>
            ) : (
              value || <span className="text-dim">—</span>
            )}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/**
 * Asks before a note goes: the API's `DELETE` answers only 204, so the notes that link to it (whose links then
 * lead nowhere) are named here first. Afterwards the folder's next note opens, or the vaults.
 */
export function DeleteNoteDialog({ path, onClose }: { path: string; onClose(): void }) {
  const backlinks = useBacklinks(path);
  const remove = useDeleteNote();
  const navigate = useNavigate();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const sources = [...new Set((backlinks.data ?? []).map((b) => b.path))];

  const confirm = () =>
    remove.mutate(path, {
      onSuccess: (next) => {
        useEditor.getState().forgetNote(path);
        onClose();
        navigate(next ? noteUrl(next) : "/", { replace: true });
      },
    });

  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby="delete-note-title" className="k-sheet flex w-full max-w-md flex-col gap-3 rounded-[16px] border border-line bg-surface p-[18px]">
        <h2 id="delete-note-title" className="text-[20px] font-bold text-text">
          Delete {titleOf(path)}?
        </h2>
        <p className="text-text-body">It goes to the vault's trash.</p>
        {backlinks.isPending ? (
          <p className="text-sm text-muted">Looking for notes that link here …</p>
        ) : backlinks.isError ? (
          <p className="text-sm text-muted">Couldn't check which notes link here.</p>
        ) : sources.length === 0 ? (
          <p className="text-sm text-muted">No other note links here.</p>
        ) : (
          <div>
            <p className="text-sm text-text-body">{sources.length === 1 ? "This note links here; its link will lead nowhere:" : "These notes link here; their links will lead nowhere:"}</p>
            <ul className="mt-1.5 flex max-h-40 flex-col gap-1 overflow-y-auto">
              {sources.map((s) => (
                <li key={s} className="flex items-baseline gap-2 text-text">
                  <span className="truncate">{titleOf(s)}</span>
                  <span className="truncate font-mono text-[11px] text-dim">{crumbOf(s)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {remove.isError && (
          <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
            {errorText(remove.error)}
          </p>
        )}
        <div className="mt-1 flex gap-2.5">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button
            type="button"
            disabled={remove.isPending}
            onClick={confirm}
            className="min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
          >
            Delete
          </button>
        </div>
      </div>
    </div>
  );
}
