// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useRef, useState } from "react";
import type { SaveResult } from "../api/notes";

/** MOBILE_EDITOR §7: a save 800 ms after the last keystroke. */
export const AUTOSAVE_DELAY = 800;

/** The note as the server has it after a write by someone else: what a 409 answered. */
export type Conflict = { content: string; version: string };
/** `keepalive`: the page is going away, and the request has to outlive it. */
export type SaveOptions = { keepalive: boolean };
export type AutosaveStatus = "saved" | "pending" | "saving" | "conflict" | "error";

/**
 * The text being written and its saves. `draft` is text left unsaved by a session that ended: it is saved on its
 * own version at once, so a newer write meanwhile ends in the conflict toast. Saves go one at a time, each on the version the previous one returned
 * (never one only read from the cache, which may already hold an agent's newer write). A 409 pauses saving and
 * keeps the user's text until they choose: `reload` takes the newer text, `keepMine` writes theirs on top of it.
 */
export function useAutosave(
  initial: { content: string; version: string },
  save: (content: string, version: string, options: SaveOptions) => Promise<SaveResult>,
  draft: { text: string; version: string } | null = null,
) {
  const [value, setValue] = useState(draft?.text ?? initial.content);
  const [status, setStatus] = useState<AutosaveStatus>("saved");
  const [conflict, setConflict] = useState<Conflict | null>(null);
  const saveRef = useRef(save);
  saveRef.current = save;
  // The live state, read by timers and promises that outlast a render.
  const s = useRef({
    text: draft?.text ?? initial.content,
    /** What the server holds at `version`, as far as this editor knows. */
    saved: initial.content,
    version: draft?.version ?? initial.version,
    timer: undefined as ReturnType<typeof setTimeout> | undefined,
    inFlight: null as Promise<void> | null,
    again: false,
    /** The next save is the last one before the page goes away. */
    keepalive: false,
    conflict: null as Conflict | null,
    failed: false,
    /** The note is gone from its path: nothing is saved any more (a save would answer 404, or make it again). */
    stopped: false,
  }).current;

  const run = useCallback((keepalive = false): Promise<void> => {
    if (keepalive) s.keepalive = true;
    clearTimeout(s.timer);
    s.timer = undefined;
    if (s.conflict || s.stopped) return Promise.resolve();
    if (s.inFlight) {
      s.again = true;
      return s.inFlight;
    }
    if (s.text === s.saved) {
      s.failed = false;
      setStatus("saved");
      return Promise.resolve();
    }
    const content = s.text;
    const options = { keepalive: s.keepalive };
    s.keepalive = false;
    s.failed = false;
    setStatus("saving");
    s.inFlight = saveRef
      .current(content, s.version, options)
      .then(
        (result) => {
          if ("saved" in result) {
            s.version = result.saved.version;
            s.saved = content;
          } else {
            s.conflict = result.conflict;
          }
        },
        () => {
          s.failed = true;
        },
      )
      .then(() => {
        s.inFlight = null;
        const again = s.again;
        s.again = false;
        if (s.conflict) {
          setConflict(s.conflict);
          setStatus("conflict");
        } else if (s.failed) setStatus("error");
        else if (again) void run();
        else setStatus(s.text === s.saved ? "saved" : "pending");
      });
    return s.inFlight;
  }, [s]);

  const change = useCallback(
    (text: string) => {
      s.text = text;
      setValue(text);
      if (s.conflict) return;
      clearTimeout(s.timer);
      s.timer = setTimeout(() => void run(), AUTOSAVE_DELAY);
      if (!s.inFlight && !s.failed) setStatus("pending");
    },
    [s, run],
  );

  /** Saves at once (blur; with `keepalive` when the page is hidden or closing). */
  const saveNow = useCallback((options?: SaveOptions) => void run(options?.keepalive), [run]);

  /** Saves everything still pending and answers whether all of it is saved; a failed save is tried once more. */
  const flush = useCallback(async () => {
    if (s.failed && !s.conflict) s.failed = false;
    for (;;) {
      if (s.inFlight) {
        await s.inFlight;
        continue;
      }
      if (s.text === s.saved) return !s.conflict && !s.failed;
      if (s.conflict || s.failed || s.stopped) return false;
      await run();
    }
  }, [s, run]);

  /** The user's text goes on top of the newer version; a further write meanwhile is another conflict. */
  const keepMine = useCallback(() => {
    if (!s.conflict) return Promise.resolve();
    s.version = s.conflict.version;
    s.saved = s.conflict.content;
    s.conflict = null;
    setConflict(null);
    return run();
  }, [s, run]);

  /** The newer version replaces the user's text; that is what they chose. */
  const reload = useCallback(() => {
    if (!s.conflict) return;
    clearTimeout(s.timer);
    s.text = s.saved = s.conflict.content;
    s.version = s.conflict.version;
    s.conflict = null;
    setConflict(null);
    setValue(s.text);
    setStatus("saved");
  }, [s]);

  /**
   * A newer version written elsewhere, seen before any save ran into it: the same choice as a 409's, and saving
   * pauses until it is made. Not while a save is under way: its answer decides (a 409 brings the same choice).
   */
  const offer = useCallback(
    (newer: Conflict) => {
      if (s.conflict || s.inFlight) return !!s.conflict;
      clearTimeout(s.timer);
      s.timer = undefined;
      s.conflict = newer;
      setConflict(newer);
      setStatus("conflict");
      return true;
    },
    [s],
  );

  /** The note is gone: saving stops, and the text stays here until the user copies it or saves it elsewhere. */
  const stop = useCallback(() => {
    s.stopped = true;
    clearTimeout(s.timer);
    s.timer = undefined;
  }, [s]);

  /** The text is safe elsewhere now (saved as another note): leaving no longer loses it. */
  const release = useCallback(() => {
    s.saved = s.text;
    setStatus("saved");
  }, [s]);

  const retry = useCallback(() => {
    s.failed = false;
    void run();
  }, [s, run]);

  /** Anything unsaved, in flight, failed or in conflict: leaving now could lose text. */
  const unresolved = useCallback(() => !!(s.conflict || s.failed || s.inFlight || s.text !== s.saved), [s]);

  /** The text and the version it goes on, while any of it isn't saved. */
  const unsaved = useCallback(() => (s.conflict || s.text !== s.saved ? { text: s.text, version: s.version } : null), [s]);

  useEffect(() => {
    if (s.text !== s.saved) void run();
  }, [s, run]);

  // Leaving without the guards (signed out, say): what can still be saved is sent.
  useEffect(
    () => () => {
      clearTimeout(s.timer);
      if (s.conflict || s.stopped || s.text === s.saved) return;
      // A save in flight still sends what was typed after it started, once it returns.
      if (s.inFlight) s.again = true;
      else void saveRef.current(s.text, s.version, { keepalive: false }).catch(() => {});
    },
    [s],
  );

  return { value, status, conflict, change, saveNow, flush, keepMine, reload, offer, stop, release, retry, unresolved, unsaved };
}
