// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
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

  const submit = (event: FormEvent) => {
    event.preventDefault();
    // Read from the store, not this render: a running command's controller must stay the one leaving aborts.
    if (useConsole.getState().running) return;
    controller.current = new AbortController();
    setValue("");
    setWalk(null);
    following.current = true;
    void run(value, controller.current.signal);
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
    <main className="mx-auto flex h-dvh w-full max-w-5xl flex-col gap-4 px-4 pt-10 pb-[max(16px,env(safe-area-inset-bottom))]">
      <header className="flex flex-col gap-2">
        <Link to="/" className="flex min-h-11 items-center self-start text-accent-text">
          Back to the notes
        </Link>
        <h1 className="text-[28px] leading-tight font-bold text-text">Console</h1>
      </header>
      <div
        ref={log}
        role="log"
        aria-label="Console output"
        aria-busy={running !== null}
        onScroll={(event) => {
          const { scrollHeight, scrollTop, clientHeight } = event.currentTarget;
          following.current = scrollHeight - scrollTop - clientHeight < 8;
        }}
        // A click in the output goes on typing, unless it selected something to copy.
        onClick={() => !window.getSelection()?.toString() && input.current?.focus()}
        className="min-h-0 flex-1 overflow-y-auto font-mono text-sm text-text"
      >
        <p className={`mb-4 text-text-body ${wrap}`}>{welcome}</p>
        {entries.map((entry, i) => (
          <EntryView key={entry.id} entry={entry} withPath={i === 0 || entries[i - 1].cwd !== entry.cwd} onImage={follow} />
        ))}
        {running && <EntryView entry={running} withPath={entries.length === 0 || entries[entries.length - 1].cwd !== running.cwd} onImage={follow} />}
      </div>
      <form aria-label="Command line" onSubmit={submit} className="font-mono text-sm">
        <PathLine cwd={cwd} />
        <div className="flex items-center gap-2">
          <span aria-hidden className="text-muted">
            &gt;
          </span>
          <input
            ref={input}
            aria-label="Command"
            value={value}
            onChange={(event) => setValue(event.target.value)}
            onKeyDown={onKeyDown}
            disabled={running !== null}
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            enterKeyHint="go"
            // 16 px so iOS doesn't zoom in.
            className="min-h-11 min-w-0 flex-1 bg-transparent font-mono text-base text-text focus:outline-none disabled:opacity-60"
          />
        </div>
      </form>
    </main>
  );
}

/** The working directory: its vault in the accent colour, the rest muted. */
function PathLine({ cwd }: { cwd: string }) {
  const at = cwd.indexOf("/", 1);
  const [vault, rest] = cwd === "/" ? ["", "/"] : at < 0 ? [cwd, ""] : [cwd.slice(0, at), cwd.slice(at)];
  return (
    <div data-line className="break-words">
      {vault && <span className="text-accent-text">{vault}</span>}
      {rest && <span className="text-muted">{rest}</span>}
    </div>
  );
}

function EntryView({ entry, withPath, onImage }: { entry: Entry | Running; withPath: boolean; onImage(): void }) {
  return (
    <div className="mb-2">
      {withPath && <PathLine cwd={entry.cwd} />}
      <div data-line className={wrap}>
        <span aria-hidden className="select-none text-muted">
          {"> "}
        </span>
        {entry.typed}
      </div>
      {entry.sent !== undefined && (
        <div data-line className={`text-dim ${wrap}`}>
          <span aria-hidden className="select-none">
            {"→ "}
          </span>
          {entry.sent}
        </div>
      )}
      {"output" in entry && entry.output.map((output, i) => <OutputView key={i} output={output} onImage={onImage} />)}
    </div>
  );
}

function OutputView({ output, onImage }: { output: Output; onImage(): void }) {
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
    <div data-line className={`text-warning ${wrap}`}>
      <span aria-hidden className="select-none">
        {"! "}
      </span>
      <span className="sr-only">Error: </span>
      {output.text}
    </div>
  );
}
