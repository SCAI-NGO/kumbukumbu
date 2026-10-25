// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useRef, useState } from "react";
import { useBlocker, type BlockerFunction } from "react-router-dom";
import { useSaveNote, type NoteView } from "../api/notes";
import { useEditor } from "../state/editor";
import ConflictToast, { ConflictDiff } from "./ConflictToast";
import MarkdownToolbar from "./MarkdownToolbar";
import { useAutosave } from "./useAutosave";

/**
 * Write mode (MOBILE_EDITOR §4, §7): the raw Markdown in a plain textarea (16 px, so iOS doesn't zoom), saved as
 * the user types. Nothing leaves Write mode, the note or the page while text is unsaved without saving it first;
 * with a conflict or a failed save open, the user stays here and decides.
 */
export default function MarkdownEditor({ path, note }: { path: string; note: NoteView }) {
  const save = useSaveNote(path);
  const autosave = useAutosave(note, (content, version) => save.mutateAsync({ content, version }));
  const { value, status, conflict, change, saveNow, flush, keepMine, reload, retry, unresolved } = autosave;
  const textarea = useRef<HTMLTextAreaElement>(null);
  const [diff, setDiff] = useState(false);

  useEffect(() => {
    textarea.current?.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    useEditor.getState().setBeforeRead(flush);
    return () => useEditor.getState().setBeforeRead(null);
  }, [flush]);

  useEffect(() => {
    const onHidden = () => document.visibilityState === "hidden" && saveNow();
    const warn = (e: BeforeUnloadEvent) => unresolved() && e.preventDefault();
    document.addEventListener("visibilitychange", onHidden);
    window.addEventListener("beforeunload", warn);
    return () => {
      document.removeEventListener("visibilitychange", onHidden);
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
      <label htmlFor="markdown-source" className="sr-only">
        Markdown source
      </label>
      <textarea
        id="markdown-source"
        ref={textarea}
        value={value}
        onChange={(e) => change(e.target.value)}
        onBlur={saveNow}
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
      <MarkdownToolbar textarea={textarea} onChange={change} onDone={() => void useEditor.getState().endWrite()} />
    </>
  );
}
