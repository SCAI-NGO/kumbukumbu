// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type FormEvent, type KeyboardEvent } from "react";
import { useLinks, useNote } from "../api/notes";
import { useErrorTextFor } from "../discreet/errorText";
import { useGoneWithVaults } from "../discreet/state";
import { useDesktop } from "../hooks/useDesktop";
import { blockSource, findBlock } from "../note/blocks";
import { maskedBody } from "../note/comments";
import MarkdownView from "../note/MarkdownView";
import { Properties } from "../note/NoteView";
import { section } from "../note/outline";
import { splitFrontmatter } from "../note/properties";
import WindowFrame from "../windows/WindowFrame";
import { useCloseWindow } from "../windows/context";
import { complete, type Completion } from "./complete";
import { consoleSchemes, keepConsoleScheme, phosphor, storedConsoleScheme, type ConsoleScheme } from "./crt";
import { listFolder } from "./entries";
import { listMatches, run } from "./run";
import { useConsole, type Entry, type Output, type Running } from "./store";

const welcome = "Kumbu² console, not a bash, and read-only. Type help for the commands, help <command> for details.";
const wrap = "whitespace-pre-wrap break-words";

/**
 * The read-only console (console design §3): what an agent would send to `sh`, typed by a person. Plain DOM and
 * React text nodes only, so nothing in a note's text or a file name can act as markup or an escape sequence.
 */
export default function ConsolePage() {
  const cwd = useConsole((s) => s.cwd);
  const entries = useConsole((s) => s.entries);
  const history = useConsole((s) => s.history);
  const running = useConsole((s) => s.running);
  const generation = useConsole((s) => s.generation);
  const [value, setValue] = useState("");
  // Walking the history: where, and the line being typed before, to come back to.
  const [walk, setWalk] = useState<{ index: number; draft: string } | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const log = useRef<HTMLDivElement>(null);
  const controller = useRef<AbortController | null>(null);
  const following = useRef(true);
  const close = useCloseWindow();
  const desktop = useDesktop();
  const [scheme, setScheme] = useState(storedConsoleScheme);
  const crt = scheme !== "plain";
  // The caret's column, how far a long line scrolled the input, and whether it has the focus, for the drawn cursor.
  const [caret, setCaret] = useState({ column: 0, scrolled: 0 });
  const [focused, setFocused] = useState(false);
  const markCaret = () => setCaret({ column: input.current?.selectionStart ?? 0, scrolled: input.current?.scrollLeft ?? 0 });
  const choose = (next: ConsoleScheme) => {
    setScheme(next);
    keepConsoleScheme(next);
  };
  // Completion: where the caret goes after an insert, the line a Tab found nothing new on (the next one lists), the
  // no-match flash, and the phone's suggestions for the line and caret they were made for.
  const caretTo = useRef<number | null>(null);
  const lastTab = useRef<{ line: string; cursor: number } | null>(null);
  const [bell, setBell] = useState(false);
  const bellTimer = useRef<number | undefined>(undefined);
  // Made in the console's generation they belong to: a reset (a discreet vault hidden) takes its names with it.
  const [suggested, setSuggested] = useState<{ line: string; cursor: number; generation: number; matches: string[] } | null>(null);
  const chips =
    !desktop && running === null && value !== "" && suggested?.line === value && suggested.cursor === caret.column && suggested.generation === generation
      ? suggested.matches
      : [];

  // Leaving the page stops the running command; the store keeps the rest for coming back.
  useEffect(
    () => () => {
      controller.current?.abort();
      window.clearTimeout(bellTimer.current);
    },
    [],
  );
  useEffect(() => {
    if (!running) input.current?.focus();
  }, [running]);
  // To the newest answer, unless the user scrolled up; again once an image has its height.
  const follow = () => {
    if (following.current && log.current) log.current.scrollTop = log.current.scrollHeight;
  };
  useLayoutEffect(follow, [entries, running, chips.length > 0]);
  // Typing, history, clearing and completing move the caret too; completing puts it after what it put in.
  useLayoutEffect(() => {
    if (caretTo.current !== null) input.current?.setSelectionRange(caretTo.current, caretTo.current);
    caretTo.current = null;
    markCaret();
  }, [value]);

  // The phone's chips: for the word under the caret, and for an empty one after a command the working folder's
  // entries, to save typing on the phone; an empty first word gets none (complete offers no commands for it).
  useEffect(() => {
    if (desktop || running || value === "") return;
    const cursor = caret.column;
    let current = true;
    void complete(value, cursor, cwd, listFolder).then((result) => {
      if (current) setSuggested({ line: value, cursor, generation, matches: result.matches });
    });
    return () => {
      current = false;
    };
  }, [desktop, running, value, caret.column, cwd, generation]);

  /** Whether the line and caret are still the ones a completion was made for, and nothing runs. */
  const still = (line: string, cursor: number) =>
    !useConsole.getState().running && input.current?.value === line && input.current.selectionStart === cursor;

  /** Puts a completion in; gives the new line and caret. */
  const put = (line: string, { replaceFrom, replaceTo, insert }: Completion) => {
    const next = { line: line.slice(0, replaceFrom) + insert + line.slice(replaceTo), cursor: replaceFrom + insert!.length };
    caretTo.current = next.cursor;
    setValue(next.line);
    return next;
  };

  const ring = () => {
    setBell(true);
    window.clearTimeout(bellTimer.current);
    bellTimer.current = window.setTimeout(() => setBell(false), 200);
  };

  const tab = async () => {
    const line = input.current?.value ?? "";
    const cursor = input.current?.selectionStart ?? line.length;
    if (useConsole.getState().running) return;
    const result = await complete(line, cursor, useConsole.getState().cwd, listFolder);
    if (!still(line, cursor)) return;
    // Only a word that matches nothing flashes; an empty line or stage, an option or an unlisted folder stay quiet.
    if (result.matches.length === 0) {
      lastTab.current = null;
      if (result.replaceFrom < cursor) ring();
      return;
    }
    if (result.insert !== null && line.slice(result.replaceFrom, result.replaceTo) !== result.insert) {
      const next = put(line, result);
      // Only a common prefix went in: the next Tab lists, as bash does.
      lastTab.current = result.matches.length > 1 ? next : null;
      return;
    }
    // Nothing new twice on the same line: the matches, as zsh lists them.
    if (lastTab.current?.line === line && lastTab.current.cursor === cursor) listMatches(line, result.matches);
    lastTab.current = { line, cursor };
  };

  const take = async (name: string) => {
    if (!suggested) return;
    const { line, cursor } = suggested;
    const result = await complete(line, cursor, cwd, listFolder, name);
    if (result.insert !== null && still(line, cursor)) put(line, result);
    input.current?.focus();
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    // Read from the store, not this render: a running command's controller must stay the one leaving aborts.
    if (useConsole.getState().running) return;
    controller.current = new AbortController();
    setValue("");
    setWalk(null);
    following.current = true;
    void run(value, controller.current.signal, close);
  };

  const onKeyDown = (event: KeyboardEvent) => {
    // Tab is the console's while the input has the focus; Shift+Tab still moves it on.
    if (event.key === "Tab" && !event.shiftKey && !event.altKey && !event.ctrlKey && !event.metaKey) {
      event.preventDefault();
      void tab();
      return;
    }
    if ((event.key !== "ArrowUp" && event.key !== "ArrowDown") || history.length === 0) return;
    event.preventDefault();
    if (event.key === "ArrowUp") {
      const index = walk ? Math.max(0, walk.index - 1) : history.length - 1;
      setWalk({ index, draft: walk?.draft ?? value });
      setValue(history[index]);
    } else if (walk && walk.index + 1 < history.length) {
      setWalk({ ...walk, index: walk.index + 1 });
      setValue(history[walk.index + 1]);
    } else if (walk) {
      setWalk(null);
      setValue(walk.draft);
    }
  };

  return (
    <WindowFrame kind="console" title="Console" actions={<SchemeSwitch scheme={scheme} onChoose={choose} />}>
      {/* Only the inside glows, a screen in a case: the title bar and the border keep the app's look. */}
      <div
        data-crt={crt ? "" : undefined}
        style={crt ? ({ "--crt-fg": phosphor[scheme].fg, "--crt-bg": phosphor[scheme].bg, "--crt-dim": phosphor[scheme].dim, color: "var(--crt-fg)", backgroundColor: "var(--crt-bg)", textShadow: "0 0 6px color-mix(in srgb, var(--crt-fg) 55%, transparent)" } as CSSProperties) : undefined}
        className="relative flex h-full flex-col gap-4 px-4 pt-4 pb-[max(16px,env(safe-area-inset-bottom))]"
      >
        <div className="relative min-h-0 flex-1">
          <div
            ref={log}
            role="log"
            data-scheme={scheme}
            aria-label="Console output"
            aria-busy={running !== null}
            onScroll={(event) => {
              const { scrollHeight, scrollTop, clientHeight } = event.currentTarget;
              following.current = scrollHeight - scrollTop - clientHeight < 8;
            }}
            // A click in the output goes on typing, unless it selected something to copy.
            onClick={() => !window.getSelection()?.toString() && input.current?.focus()}
            // The chips sit over the output's foot: it grows by their height and a scrollbar's, so they never cover its last line.
            className={`h-full overflow-y-auto font-mono text-sm ${crt ? "" : "text-text"} ${chips.length > 0 ? "pb-16" : ""}`}
          >
            <p className={`mb-4 ${crt ? "" : "text-text-body"} ${wrap}`}>{welcome}</p>
            {entries.map((entry, i) => (
              <EntryView key={entry.id} entry={entry} withPath={i === 0 || entries[i - 1].cwd !== entry.cwd} crt={crt} onImage={follow} />
            ))}
            {running && <EntryView entry={running} withPath={entries.length === 0 || entries[entries.length - 1].cwd !== running.cwd} crt={crt} onImage={follow} />}
          </div>
          {crt && (
            <div
              data-scanlines
              aria-hidden="true"
              className="pointer-events-none absolute inset-0 z-10 opacity-[0.06]"
              style={{ backgroundImage: "repeating-linear-gradient(to bottom, transparent 0 2px, var(--crt-fg) 2px 3px)" }}
            />
          )}
          {chips.length > 0 && (
            <div role="group" aria-label="Suggestions" className={`absolute inset-x-0 bottom-0 z-20 flex gap-2 overflow-x-auto ${crt ? "bg-(--crt-bg)" : "bg-surface"}`}>
              {chips.map((name) => (
                <button
                  key={name}
                  type="button"
                  // The input keeps the focus, so the keyboard stays up.
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => void take(name)}
                  className={`min-h-12 shrink-0 rounded-[10px] border px-3 font-mono text-sm whitespace-pre ${crt ? "border-(--crt-fg) bg-(--crt-bg)" : "border-line bg-surface-2 text-text"}`}
                >
                  {name}
                </button>
              ))}
            </div>
          )}
        </div>
        <form aria-label="Command line" onSubmit={submit} className={`rounded-md font-mono text-sm ${bell ? "k-bell" : ""}`}>
          <PathLine cwd={cwd} crt={crt} />
          <div className="flex items-center gap-2">
            <span aria-hidden className={crt ? "text-(--crt-dim)" : "text-muted"}>
              &gt;
            </span>
            <div className="relative min-w-0 flex-1 overflow-hidden">
              <input
                ref={input}
                aria-label="Command"
                data-autofocus
                value={value}
                onChange={(event) => {
                  lastTab.current = null;
                  setValue(event.target.value);
                }}
                onKeyDown={onKeyDown}
                onKeyUp={markCaret}
                onSelect={markCaret}
                onClick={markCaret}
                onScroll={markCaret}
                onFocus={() => setFocused(true)}
                onBlur={() => setFocused(false)}
                disabled={running !== null}
                autoComplete="off"
                autoCapitalize="none"
                autoCorrect="off"
                spellCheck={false}
                enterKeyHint="go"
                // 16 px so iOS doesn't zoom in. In the phosphor schemes the real caret is hidden behind a drawn block.
                style={crt ? { caretColor: "transparent" } : undefined}
                className={`min-h-11 w-full bg-transparent font-mono text-base focus:outline-none disabled:opacity-60 ${crt ? "" : "text-text"}`}
              />
              {crt && focused && running === null && (
                // Monospace, so the caret's column is its width in characters. Steady under reduced motion, blinking otherwise.
                <span
                  data-cursor
                  aria-hidden="true"
                  style={{ left: `calc(${caret.column}ch - ${caret.scrolled}px)`, backgroundColor: "var(--crt-fg)" }}
                  className="pointer-events-none absolute top-1/2 h-[1.2em] w-[1ch] -translate-y-1/2 font-mono text-base motion-safe:animate-[k-blink_1s_steps(1)_infinite]"
                />
              )}
            </div>
          </div>
        </form>
      </div>
    </WindowFrame>
  );
}

/** The working directory: its vault in the accent colour, the rest muted. */
function PathLine({ cwd, crt }: { cwd: string; crt: boolean }) {
  const at = cwd.indexOf("/", 1);
  const [vault, rest] = cwd === "/" ? ["", "/"] : at < 0 ? [cwd, ""] : [cwd.slice(0, at), cwd.slice(at)];
  return (
    <div data-line className="break-words">
      {vault && <span className={crt ? "" : "text-accent-text"}>{vault}</span>}
      {rest && <span className={crt ? "text-(--crt-dim)" : "text-muted"}>{rest}</span>}
    </div>
  );
}

function EntryView({ entry, withPath, crt, onImage }: { entry: Entry | Running; withPath: boolean; crt: boolean; onImage(): void }) {
  return (
    <div className="mb-2">
      {withPath && <PathLine cwd={entry.cwd} crt={crt} />}
      <div data-line className={wrap}>
        <span aria-hidden className={`select-none ${crt ? "text-(--crt-dim)" : "text-muted"}`}>
          {"> "}
        </span>
        {entry.typed}
      </div>
      {entry.sent !== undefined && (
        <div data-line className={`${crt ? "text-(--crt-dim)" : "text-dim"} ${wrap}`}>
          <span aria-hidden className="select-none">
            {"→ "}
          </span>
          {entry.sent}
        </div>
      )}
      {"output" in entry && entry.output.map((output, i) => <OutputView key={i} output={output} crt={crt} onImage={onImage} />)}
    </div>
  );
}

function OutputView({ output, crt, onImage }: { output: Output; crt: boolean; onImage(): void }) {
  if (output.kind === "note") return <Peek path={output.path} at={output.at} crt={crt} onShown={onImage} />;
  if (output.kind === "image") {
    return (
      <div data-line>
        <img src={output.src} alt={output.alt} onLoad={onImage} className="my-1 h-auto max-w-full" />
      </div>
    );
  }
  if (!output.error) {
    return (
      <div data-line className={wrap}>
        {output.text}
      </div>
    );
  }
  return (
    // A phosphor screen has one colour: errors are inverse video. The word "error" shows in every scheme, never colour alone.
    <div
      data-line
      data-error={crt ? "" : undefined}
      className={`${crt ? "bg-(--crt-fg) px-1 text-(--crt-bg)" : "text-warning"} ${wrap}`}
      style={crt ? { textShadow: "none" } : undefined}
    >
      <span aria-hidden className="select-none">
        {"! "}
      </span>
      <span>Error:</span> {output.text}
    </div>
  );
}

const noop = () => {};

// On a phosphor screen the note's colours are the screen's: headings and links in its full colour, the text a little
// dimmer, so they stand out; lines and boxes in the dimmed one.
const phosphorNote = {
  "--text": "var(--crt-fg)",
  "--text-body": "color-mix(in srgb, var(--crt-fg) 80%, var(--crt-bg))",
  "--accent-text": "var(--crt-fg)",
  "--accent": "var(--crt-fg)",
  "--on-accent": "var(--crt-bg)",
  "--muted": "var(--crt-dim)",
  "--dim": "var(--crt-dim)",
  "--line": "var(--crt-dim)",
  "--warning": "var(--crt-fg)",
  "--surface": "var(--crt-bg)",
  "--surface-2": "var(--crt-bg)",
  "--highlight": "var(--crt-dim)",
} as CSSProperties;

/** A note `p` shows. Of a discreet vault hidden again, nothing of it stays: no text, path or vault, and no more reads. */
function Peek(props: { path: string; at: string | null; crt: boolean; onShown(): void }) {
  const gone = useGoneWithVaults(props.path);
  if (gone) {
    return (
      <div data-line className={props.crt ? "text-(--crt-dim)" : "text-muted"}>
        (a note that's hidden again)
      </div>
    );
  }
  return <PeekedNote {...props} />;
}

/**
 * The note as the note view reads and renders it (the same query, renderer and sanitising), whole however long, under
 * its path: its links as the server resolves them, task boxes disabled, Mermaid as code, and no note created from a
 * link to a missing one, as the console never writes; tags only name themselves. A section or a block keeps the note's lines, which its links
 * go by.
 */
function PeekedNote({ path, at, crt, onShown }: { path: string; at: string | null; crt: boolean; onShown(): void }) {
  const note = useNote(path);
  const links = useLinks(path, note.data?.version);
  const errorFor = useErrorTextFor(path);
  const content = note.data?.content;
  const shown = useMemo(() => {
    if (content === undefined) return null;
    const { body, mask } = maskedBody(content);
    const frontmatter = at === null ? splitFrontmatter(content).frontmatter : null;
    const block = at?.startsWith("^") ? at.slice(1) : null;
    const found = block === null ? null : findBlock(content, block);
    const source = block !== null ? found && blockSource(body, found) : at === null ? body : section(body, at);
    return { source, mask, frontmatter };
  }, [content, at]);
  // The log follows the newest answer; this one comes after its entry, and may end in an error after loading.
  useLayoutEffect(onShown, [shown, note.isError, onShown]);

  let state: Output | null = null;
  if (note.isError) state = { kind: "text", text: `p: ${errorFor(note.error)}`, error: true };
  else if (shown === null) state = { kind: "text", text: "Loading …" };
  else if (shown.source === null) state = { kind: "text", text: `p: ${path}: no ${at!.startsWith("^") ? `block ${at}` : `heading “${at}”`}`, error: true };
  return (
    <>
      <PathLine cwd={at === null ? path : `${path}#${at}`} crt={crt} />
      {state ? (
        <OutputView output={state} crt={crt} onImage={onShown} />
      ) : (
        <div data-peek style={crt ? phosphorNote : undefined} className="my-2 font-sans">
          {shown!.frontmatter !== null && <Properties yaml={shown!.frontmatter} changed={null} still />}
          <MarkdownView source={shown!.source!} mask={shown!.mask} links={links.data} tasksDisabled onToggleTask={noop} path={path} diagramsAsCode tagsStill />
        </div>
      )}
    </>
  );
}

const label: Record<ConsoleScheme, string> = { plain: "Plain", green: "Green", amber: "Amber" };

/** In the title bar: three buttons on the desktop, one that cycles on the phone. */
function SchemeSwitch({ scheme, onChoose }: { scheme: ConsoleScheme; onChoose(scheme: ConsoleScheme): void }) {
  const desktop = useDesktop();
  const button = "min-h-11 px-3 text-sm focus-visible:outline-2 focus-visible:outline-accent";
  if (!desktop) {
    const next = consoleSchemes[(consoleSchemes.indexOf(scheme) + 1) % consoleSchemes.length];
    return (
      <button type="button" onClick={() => onChoose(next)} className={`${button} rounded-[10px] border border-line bg-surface text-text`}>
        Console scheme: {label[scheme]}
      </button>
    );
  }
  return (
    <div role="group" aria-label="Console scheme" className="flex overflow-hidden rounded-md border border-line bg-surface-2">
      {consoleSchemes.map((option) => (
        <button
          key={option}
          type="button"
          aria-pressed={option === scheme}
          onClick={() => onChoose(option)}
          className={`${button} ${option === scheme ? "bg-surface font-medium text-text" : "text-muted"}`}
        >
          {label[option]}
        </button>
      ))}
    </div>
  );
}
