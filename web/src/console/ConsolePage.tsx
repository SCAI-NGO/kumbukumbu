// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type FormEvent, type KeyboardEvent } from "react";
import { useDesktop } from "../hooks/useDesktop";
import WindowFrame from "../windows/WindowFrame";
import { useCloseWindow } from "../windows/context";
import { consoleSchemes, keepConsoleScheme, phosphor, storedConsoleScheme, type ConsoleScheme } from "./crt";
import { run } from "./run";
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
  const [value, setValue] = useState("");
  // Walking the history: where, and the line being typed before, to come back to.
  const [walk, setWalk] = useState<{ index: number; draft: string } | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const log = useRef<HTMLDivElement>(null);
  const controller = useRef<AbortController | null>(null);
  const following = useRef(true);
  const close = useCloseWindow();
  const [scheme, setScheme] = useState(storedConsoleScheme);
  const crt = scheme !== "plain";
  // The caret's column and whether the input has the focus, for the drawn cursor.
  const [caret, setCaret] = useState(0);
  const [focused, setFocused] = useState(false);
  const markCaret = () => setCaret(input.current?.selectionStart ?? 0);
  const choose = (next: ConsoleScheme) => {
    setScheme(next);
    keepConsoleScheme(next);
  };

  // Leaving the page stops the running command; the store keeps the rest for coming back.
  useEffect(() => () => controller.current?.abort(), []);
  useEffect(() => {
    if (!running) input.current?.focus();
  }, [running]);
  // To the newest answer, unless the user scrolled up; again once an image has its height.
  const follow = () => {
    if (following.current && log.current) log.current.scrollTop = log.current.scrollHeight;
  };
  useLayoutEffect(follow, [entries, running]);
  // Typing, history and clearing move the caret too.
  useLayoutEffect(markCaret, [value]);

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
          className={`h-full overflow-y-auto font-mono text-sm ${crt ? "" : "text-text"}`}
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
        </div>
        <form aria-label="Command line" onSubmit={submit} className="font-mono text-sm">
          <PathLine cwd={cwd} crt={crt} />
          <div className="flex items-center gap-2">
            <span aria-hidden className={crt ? "text-(--crt-dim)" : "text-muted"}>
              &gt;
            </span>
            <div className="relative min-w-0 flex-1">
            <input
              ref={input}
              aria-label="Command"
              value={value}
              onChange={(event) => setValue(event.target.value)}
              onKeyDown={onKeyDown}
              onKeyUp={markCaret}
              onSelect={markCaret}
              onClick={markCaret}
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
                style={{ left: `${caret}ch`, backgroundColor: "var(--crt-fg)" }}
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

const label: Record<ConsoleScheme, string> = { plain: "Plain", green: "Green", amber: "Amber" };

/** In the title bar: three buttons on the desktop, one that cycles on the phone. */
function SchemeSwitch({ scheme, onChoose }: { scheme: ConsoleScheme; onChoose(scheme: ConsoleScheme): void }) {
  const desktop = useDesktop();
  const button = "min-h-11 px-3 text-sm focus-visible:outline-2 focus-visible:outline-accent";
  if (!desktop) {
    const next = consoleSchemes[(consoleSchemes.indexOf(scheme) + 1) % consoleSchemes.length];
    return (
      <button type="button" onClick={() => onChoose(next)} className={`${button} text-muted`}>
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
          className={`${button} ${option === scheme ? "bg-surface text-accent-text font-bold" : "text-muted"}`}
        >
          {label[option]}
        </button>
      ))}
    </div>
  );
}
