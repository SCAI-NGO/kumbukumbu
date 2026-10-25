// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useBlocker, useNavigate, type BlockerFunction } from "react-router-dom";
import { api } from "../api/client";
import { useSaveNote, type NoteView } from "../api/notes";
import { folderOf, inVault, noteUrl, titleOf, vaultOf } from "../api/paths";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { BarButton, ChangeText, ElsewhereBar, GoneText, type useChangedElsewhere } from "./ChangedElsewhere";
import ConflictToast, { ConflictDiff } from "./ConflictToast";
import { useLinkCompletion } from "./LinkCompletion";
import MarkdownToolbar from "./MarkdownToolbar";
import { useAutosave } from "./useAutosave";

/**
 * Write mode (MOBILE_EDITOR §4, §7): the raw Markdown in a plain textarea (16 px, so iOS doesn't zoom), saved as
 * the user types. Nothing leaves Write mode, the note or the page while text is unsaved without saving it first;
 * with a conflict or a failed save open, the user stays here and decides. A session that ends meanwhile leaves the
 * unsaved text in memory (never in storage), and the note opens with it again once the same user is back.
 */
export default function MarkdownEditor({ path, note, elsewhere }: { path: string; note: NoteView; elsewhere?: ReturnType<typeof useChangedElsewhere> }) {
  const save = useSaveNote(path);
  const queryClient = useQueryClient();
  const [draft] = useState(() => {
    const { draft } = useEditor.getState();
    const mine = draft?.path === path && draft.username === useSession.getState().user?.username;
    return mine && draft.text !== note.content ? draft : null;
  });
  const autosave = useAutosave(
    note,
    // Without a session a save can only fail (401); what is unsaved is kept as a draft instead (below).
    (content, version, options) => (useSession.getState().user ? save.mutateAsync({ content, version, ...options }) : Promise.reject(new Error("Signed out"))),
    draft,
  );
  const { value, status, conflict, change, saveNow, flush, keepMine, reload, offer, stop, release, retry, unresolved, unsaved } = autosave;
  const navigate = useNavigate();
  const textarea = useRef<HTMLTextAreaElement>(null);
  const [diff, setDiff] = useState(false);
  // A version written elsewhere is shown, never put in place of the user's text; "Keep writing" hides it.
  const [dismissed, setDismissed] = useState<string | null>(null);
  const gone = !!elsewhere?.gone;
  const newer = gone ? null : elsewhere?.change;
  useEffect(() => {
    if (gone) stop();
  }, [gone, stop]);
  const [keepProblem, setKeepProblem] = useState<string | null>(null);

  /** The text as a new note beside where the note was: `plan (kept).md`, else `plan (kept 2).md` …, opened in Write mode. */
  const saveAsNew = async () => {
    setKeepProblem(null);
    const text = value;
    for (let i = 1; i <= 20; i++) {
      const target = `${folderOf(path)}/${titleOf(path)} (kept${i === 1 ? "" : ` ${i}`}).md`;
      try {
        const { response } = await api.PUT("/api/notes/{vault}/{path}", { params: { path: { vault: vaultOf(target), path: inVault(target) } }, body: { content: text } });
        if (response.status === 409) continue;
        if (!response.ok) break;
      } catch {
        break;
      }
      release();
      void queryClient.invalidateQueries({ queryKey: ["tree", vaultOf(target)] });
      useEditor.getState().openNote(target, "write");
      navigate(noteUrl(target));
      return;
    }
    setKeepProblem("Couldn't save it as a new note. Copy the text instead.");
  };
  const copy = () => void navigator.clipboard?.writeText(value).catch(() => setKeepProblem("Couldn't copy the text. Select it and copy it by hand."));
  const viewNewer = async () => {
    await queryClient.refetchQueries({ queryKey: ["note", path], exact: true });
    const fresh = queryClient.getQueryData<NoteView>(["note", path]);
    if (fresh && offer({ content: fresh.content, version: fresh.version })) setDiff(true);
  };
  const completion = useLinkCompletion(path, textarea, change);

  // Before the browser paints: the editor is there focused, also when it arrives late (it is loaded on first use).
  useLayoutEffect(() => {
    textarea.current?.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    // Taken: from here on the editor holds the text.
    if (useEditor.getState().draft) useEditor.getState().setDraft(null);
  }, []);

  useEffect(() => {
    // The session ended under the editor (a 401): the user is gone, and whose it was is still known. A sign-out
    // forgets that, as the user chose to leave.
    return () => {
      const { user, previousUser } = useSession.getState();
      const text = unsaved();
      if (!user && previousUser && text) useEditor.getState().setDraft({ username: previousUser, path, ...text });
    };
  }, [path, unsaved]);

  useEffect(() => {
    useEditor.getState().setBeforeRead(async ({ reread = true } = {}) => {
      const done = await flush();
      // A read of the note that started before the last save (a focus refetch) may answer after it, with the
      // older version: asked again, Read mode shows what was saved.
      if (done && reread) void queryClient.invalidateQueries({ queryKey: ["note", path], exact: true });
      return done;
    });
    return () => useEditor.getState().setBeforeRead(null);
  }, [flush, queryClient, path]);

  useEffect(() => {
    // Mobile browsers rarely fire beforeunload (iOS Safari never): hidden and pagehide are the last moments to
    // save, and keepalive lets the request outlive the page. Nothing is kept on the device instead (no plaintext).
    const leaving = () => saveNow({ keepalive: true });
    const onHidden = () => document.visibilityState === "hidden" && leaving();
    const warn = (e: BeforeUnloadEvent) => {
      if (!unresolved()) return;
      e.preventDefault();
      // Older browsers ask only when returnValue is set.
      e.returnValue = "";
    };
    document.addEventListener("visibilitychange", onHidden);
    window.addEventListener("pagehide", leaving);
    window.addEventListener("beforeunload", warn);
    return () => {
      document.removeEventListener("visibilitychange", onHidden);
      window.removeEventListener("pagehide", leaving);
      window.removeEventListener("beforeunload", warn);
    };
  }, [saveNow, unresolved]);

  // Opening another note: openNote() has already moved the store on (and to Read mode) when the navigation
  // comes here. While saves are pending the store goes back to this note, and on to the other once they are done.
  const held = useRef<{ currentPath: string | null; mode: "read" | "write" } | null>(null);
  const shouldBlock = useCallback<BlockerFunction>(
    ({ currentLocation, nextLocation }) => {
      // A session that ended goes to sign-in regardless; the text can't be saved without one.
      if (currentLocation.pathname === nextLocation.pathname || nextLocation.pathname === "/signin" || !unresolved()) return false;
      const { currentPath, mode } = useEditor.getState();
      held.current = { currentPath, mode };
      useEditor.setState({ currentPath: path, mode: "write" });
      return true;
    },
    [path, unresolved],
  );
  const blocker = useBlocker(shouldBlock);
  useEffect(() => {
    if (blocker.state !== "blocked") return;
    void flush().then((done) => {
      if (!done) return blocker.reset();
      blocker.proceed();
      if (held.current) useEditor.setState(held.current);
    });
  }, [blocker, flush]);

  const chooseKeepMine = () => {
    setDiff(false);
    void keepMine();
  };
  const chooseReload = () => {
    setDiff(false);
    reload();
  };

  return (
    <>
      {gone && (
        <ElsewhereBar
          actions={
            <>
              <BarButton onClick={copy}>Copy text</BarButton>
              <BarButton onClick={() => void saveAsNew()}>Save as new note</BarButton>
            </>
          }
        >
          <GoneText movedTo={elsewhere?.movedTo ?? null} /> Your text is kept here, but no longer saved: copy it, or save it as a new note.
          {keepProblem && <span className="block text-text">{keepProblem}</span>}
        </ElsewhereBar>
      )}
      {!conflict && newer && dismissed !== newer.version && (
        <ElsewhereBar
          actions={
            <>
              <BarButton onClick={() => void viewNewer()}>View the new version</BarButton>
              <BarButton onClick={() => setDismissed(newer.version)}>Keep writing</BarButton>
            </>
          }
        >
          <ChangeText change={newer} />
        </ElsewhereBar>
      )}
      <label htmlFor="markdown-source" className="sr-only">
        Markdown source
      </label>
      <textarea
        id="markdown-source"
        ref={textarea}
        value={value}
        onChange={(e) => {
          change(e.target.value);
          completion.follow();
        }}
        onSelect={completion.follow}
        onKeyDown={completion.onKeyDown}
        {...completion.textareaProps}
        onBlur={() => saveNow()}
        spellCheck={false}
        className="min-h-0 w-full flex-1 resize-none border-0 bg-bg px-[22px] py-5 font-mono text-[16px] leading-[1.65] text-text-body caret-accent outline-none"
      />
      {status === "error" && (
        <div role="alert" aria-label="Not saved" className="mx-3 mb-2 flex flex-none items-center rounded-[12px] border border-line bg-surface-2 py-1 pr-1.5 pl-3.5 text-sm text-text">
          <span className="mr-0.5">{"Not saved — "}</span>
          <button type="button" onClick={retry} className="min-h-11 px-1.5 font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent">
            retry
          </button>
        </div>
      )}
      {conflict && <ConflictToast onReload={chooseReload} onKeepMine={chooseKeepMine} onViewDiff={() => setDiff(true)} />}
      {diff && conflict && <ConflictDiff theirs={conflict.content} mine={value} onReload={chooseReload} onKeepMine={chooseKeepMine} onClose={() => setDiff(false)} />}
      {completion.list}
      <MarkdownToolbar textarea={textarea} onChange={change} onDone={() => void useEditor.getState().endWrite()} />
    </>
  );
}
