// SPDX-License-Identifier: AGPL-3.0-or-later
import { createContext, useContext, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { makeAiLink, readAgain, readFile, readWithCode, type AiLinkResult, type ReaderCopy, type ReadResult } from "./readerApi";
import { snapshotText, untilText } from "./snapshot";

type State = { kind: "loading" } | { kind: "failed" } | ReadResult;

const offSentence = "Published notes can't be read on this server.";
const busySentence = "Too many tries; wait a minute and try again.";

const buttonClass =
  "min-h-11 rounded-[8px] border border-line bg-surface px-4 text-[14px] text-text focus-visible:outline-2 focus-visible:outline-accent disabled:opacity-60";

const webLink = (href: string | undefined) => {
  try {
    return !!href && ["http:", "https:", "mailto:"].includes(new URL(href).protocol);
  } catch {
    return false;
  }
};

/** A file of the copy, fetched with the reader cookie and the copy's id, and shown from a blob URL (the page's CSP allows no other). */
function CopiedImage({ copy, index, alt }: { copy: string; index: number; alt: string }) {
  const [src, setSrc] = useState<string>();
  const [broken, setBroken] = useState(false);
  useEffect(() => {
    let url: string | undefined;
    let gone = false;
    readFile(copy, index).then(
      (blob) => {
        if (gone) return;
        url = URL.createObjectURL(blob);
        setSrc(url);
      },
      () => undefined,
    );
    return () => {
      gone = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [copy, index]);
  if (broken) return <span className="font-mono text-[0.85em] text-muted">{alt}</span>;
  return src ? <img src={src} alt={alt} onError={() => setBroken(true)} className="my-3 max-w-full rounded-[8px]" /> : null;
}

/** The copy's id and the indices of its files: an `f/<n>` that names none of them is no image. */
const Files = createContext<{ copy: string; indices: ReadonlySet<number> }>({ copy: "", indices: new Set() });

// Only the copy's own files (`f/<n>`): an image from anywhere else would tell a third party who reads, and when.
function CopyImage({ src, alt }: { src?: string; alt?: string }) {
  const files = useContext(Files);
  const index = src === undefined ? undefined : /^f\/(\d+)$/.exec(src)?.[1];
  return index !== undefined && files.indices.has(Number(index)) ? <CopiedImage copy={files.copy} index={Number(index)} alt={alt ?? ""} /> : null;
}

const remarkPlugins = [remarkGfm];

// At module level, so a render keeps each component, and its images, as they are. The title is the page's h1: the
// note's headings go one level down.
const components: Components = {
  h1: ({ node: _, ...props }) => <h2 className="mt-6 mb-3 text-[24px] leading-tight font-bold text-text" {...props} />,
  h2: ({ node: _, ...props }) => <h3 className="mt-6 mb-2.5 text-[19px] font-bold text-text" {...props} />,
  h3: ({ node: _, ...props }) => <h4 className="mt-5 mb-2 text-[17px] font-bold text-text" {...props} />,
  h4: ({ node: _, ...props }) => <h5 className="mt-4 mb-1.5 text-[16px] font-bold text-text" {...props} />,
  h5: ({ node: _, ...props }) => <h6 className="mt-4 mb-1.5 text-[15px] font-bold text-text-body" {...props} />,
  h6: ({ node: _, ...props }) => <h6 className="mt-4 mb-1.5 text-[14px] font-semibold text-muted" {...props} />,
  p: ({ node: _, ...props }) => <p className="my-3" {...props} />,
  ul: ({ node: _, ...props }) => <ul className="my-3 list-disc pl-6" {...props} />,
  ol: ({ node: _, ...props }) => <ol className="my-3 list-decimal pl-6" {...props} />,
  li: ({ node: _, ...props }) => <li className="my-1 marker:text-dim" {...props} />,
  blockquote: ({ node: _, ...props }) => <blockquote className="my-3 border-l-[3px] border-accent pl-3.5 [&>p]:my-1" {...props} />,
  pre: ({ node: _, ...props }) => (
    <pre className="my-3 overflow-x-auto rounded-[10px] border border-line bg-surface px-3.5 py-3 font-mono text-[12.5px] leading-[1.6] [&>code]:bg-transparent [&>code]:p-0" {...props} />
  ),
  code: ({ node: _, ...props }) => <code className="rounded-[4px] bg-surface-2 px-[5px] py-px font-mono text-[0.85em]" {...props} />,
  strong: ({ node: _, ...props }) => <strong className="font-bold text-text" {...props} />,
  hr: () => <hr className="my-6 border-line" />,
  table: ({ node: _, ...props }) => (
    <div className="my-3 overflow-x-auto">
      <table className="border-collapse text-[15px] [&_td]:border [&_td]:border-line [&_td]:px-2 [&_td]:py-1 [&_th]:border [&_th]:border-line [&_th]:px-2 [&_th]:py-1 [&_th]:text-left" {...props} />
    </div>
  ),
  // Only web and mail links; a web link opens in a new tab that learns nothing of this page. Any other link is its text.
  a: ({ href, children }) =>
    webLink(href) ? (
      <a
        href={href}
        {...(href!.startsWith("mailto:") ? {} : { target: "_blank" })}
        rel="noopener noreferrer nofollow"
        className="text-accent-text underline decoration-accent/45 underline-offset-[3px]"
      >
        {children}
      </a>
    ) : (
      <>{children}</>
    ),
  img: ({ src, alt }) => <CopyImage src={typeof src === "string" ? src : undefined} alt={alt} />,
};

/** The copy as one Markdown text, title first, for pasting into an AI's chat: its fetch tools can't read this page. */
export const copyMarkdown = (copy: ReaderCopy) => `# ${copy.title}\n\n${copy.markdown}`;

/** What a copy did, said for a few seconds (the app's `useCopied`, which this page can't import). */
function useSaid() {
  const [said, setSaid] = useState<{ text: string; n: number } | null>(null);
  useEffect(() => {
    if (!said) return;
    const timer = setTimeout(() => setSaid(null), 3000);
    return () => clearTimeout(timer);
  }, [said]);
  const copyText = (text: string) =>
    navigator.clipboard.writeText(text).then(
      () => setSaid((s) => ({ text: "Copied", n: (s?.n ?? 0) + 1 })),
      () => setSaid((s) => ({ text: "Couldn't copy. Select the text and copy it by hand.", n: (s?.n ?? 0) + 1 })),
    );
  const saying = <div aria-live="polite">{said && <span key={said.n} className="text-[14px] text-muted">{said.text}</span>}</div>;
  return { copyText, saying };
}

/** Copies the copy's Markdown and says how that went. */
function CopyMarkdown({ copy }: { copy: ReaderCopy }) {
  const { copyText, saying } = useSaid();
  return (
    <>
      <button type="button" onClick={() => void copyText(copyMarkdown(copy))} className={buttonClass}>
        Copy as Markdown
      </button>
      {saying}
    </>
  );
}

/** Makes a new link for an AI's fetch tool with each press; the latest replaces whatever the one before showed. */
function AiLink({ copy }: { copy: string }) {
  const [made, setMade] = useState<AiLinkResult | { kind: "failed" } | null>(null);
  const [pending, setPending] = useState(false);
  const { copyText, saying } = useSaid();
  const make = () => {
    setPending(true);
    makeAiLink(copy).then(
      (result) => {
        setPending(false);
        setMade(result);
      },
      () => {
        setPending(false);
        setMade({ kind: "failed" });
      },
    );
  };
  const refusal =
    made?.kind === "none"
      ? "The link couldn't be made: open the note's link again."
      : made?.kind === "off"
        ? offSentence
        : made?.kind === "busy"
          ? busySentence
          : made?.kind === "failed"
            ? "The link couldn't be made; try again later."
            : undefined;
  return (
    <>
      <button type="button" onClick={make} disabled={pending} aria-busy={pending} className={buttonClass}>
        Link for your AI
      </button>
      {made?.kind === "link" && (
        <div className="flex w-full min-w-0 flex-wrap items-center gap-3">
          <input
            readOnly
            value={made.link}
            aria-label="Link for your AI"
            onFocus={(e) => e.currentTarget.select()}
            className="min-h-11 w-full min-w-0 rounded-[8px] border border-line bg-surface px-3 font-mono text-[13px] text-text focus-visible:outline-2 focus-visible:outline-accent sm:w-auto sm:flex-1"
          />
          <button type="button" onClick={() => void copyText(made.link)} className={buttonClass}>
            Copy
          </button>
          {saying}
        </div>
      )}
      <p aria-live="polite" className="w-full text-[14px] text-muted empty:hidden">
        {made?.kind === "link"
          ? `Valid until ${untilText(made.expiresAt)}. Anyone with this link can read this note until then: it is for an AI to fetch, not to post in a chat.`
          : refusal}
      </p>
    </>
  );
}

function Copy({ copy }: { copy: ReaderCopy }) {
  const files = useMemo(() => ({ copy: copy.copy, indices: new Set(copy.files.map((f) => f.index)) }), [copy.copy, copy.files]);
  return (
    <article>
      <h1 className="text-[28px] leading-tight font-bold text-text">{copy.title}</h1>
      <p className="mt-1 mb-4 font-mono text-[12px] text-muted">{snapshotText(copy.snapshotAt)}</p>
      <div className="mb-6 flex flex-wrap items-center gap-3">
        <CopyMarkdown copy={copy} />
        <AiLink copy={copy.copy} />
      </div>
      <div className="text-[16px] leading-[1.6] break-words text-text-body">
        <Files.Provider value={files}>
          <ReactMarkdown remarkPlugins={remarkPlugins} skipHtml components={components}>
            {copy.markdown}
          </ReactMarkdown>
        </Files.Provider>
      </div>
    </article>
  );
}

function CodeForm({ wrong, pending, onRead }: { wrong: boolean; pending: boolean; onRead: (code: string) => void }) {
  const [code, setCode] = useState("");
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (code.trim()) onRead(code.trim());
  };
  return (
    <form onSubmit={submit} aria-label="Read a published note" aria-busy={pending} className="flex flex-col gap-3">
      <label htmlFor="reader-code" className="text-[15px] text-text">
        Enter the code you were given
      </label>
      <input
        id="reader-code"
        value={code}
        onChange={(e) => setCode(e.target.value)}
        autoComplete="off"
        autoCapitalize="characters"
        autoCorrect="off"
        spellCheck={false}
        aria-invalid={wrong}
        aria-describedby={wrong ? "reader-code-wrong" : undefined}
        className="min-h-11 rounded-[8px] border border-line bg-surface px-3 font-mono text-[15px] text-text focus-visible:outline-2 focus-visible:outline-accent"
      />
      {wrong && (
        <p id="reader-code-wrong" role="alert" className="text-[14px] text-warning">
          This code doesn't open a published note.
        </p>
      )}
      <button
        type="submit"
        disabled={pending}
        className="min-h-11 self-start rounded-[8px] bg-accent px-4 font-medium text-on-accent focus-visible:outline-2 focus-visible:outline-accent disabled:opacity-60"
      >
        Read
      </button>
    </form>
  );
}

export default function Reader() {
  const [state, setState] = useState<State>({ kind: "loading" });
  const [wrong, setWrong] = useState(false);
  const [pending, setPending] = useState(false);
  const started = useRef(false);
  const latest = useRef(0);

  // Only the latest request's answer counts: an earlier one that comes in late would replace what it shows.
  const read = (request: Promise<ReadResult>, withCode: boolean) => {
    const id = ++latest.current;
    setPending(true);
    const settle = (next: State, wrongCode: boolean) => {
      if (id !== latest.current) return;
      setPending(false);
      setWrong(wrongCode);
      setState(next);
    };
    request.then(
      (result) => settle(result, withCode && result.kind === "none"),
      () => settle({ kind: "failed" }, false),
    );
  };

  useEffect(() => {
    // Once, even when React runs effects twice: the second run would find the fragment gone.
    if (started.current) return;
    started.current = true;
    const code = location.hash.slice(1);
    if (code) {
      // Out of the address bar, the history and any later copy of the URL before the code goes anywhere.
      history.replaceState(null, "", location.pathname + location.search);
      read(readWithCode(code), true);
    } else {
      read(readAgain(), false);
    }
  }, []);

  return (
    <main className="mx-auto max-w-[720px] px-5 py-10">
      {state.kind === "loading" && <p className="text-muted">Loading …</p>}
      {state.kind === "copy" && <Copy copy={state.copy} />}
      {state.kind === "none" && <CodeForm wrong={wrong} pending={pending} onRead={(code) => read(readWithCode(code), true)} />}
      {state.kind === "off" && <p className="text-text-body">{offSentence}</p>}
      {state.kind === "busy" && <p className="text-text-body">{busySentence}</p>}
      {state.kind === "failed" && <p className="text-text-body">The note couldn't be loaded; try again later.</p>}
    </main>
  );
}
