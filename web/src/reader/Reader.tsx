// SPDX-License-Identifier: AGPL-3.0-or-later
import { createContext, useContext, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import ReactMarkdown, { defaultUrlTransform, type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { remarkHideBlockIds } from "./blockIds";
import { atxHeading, headingText } from "./headings";
import { makeAiLink, readAgain, readFile, readWithCode, type AiLinkResult, type ReaderCopy, type ReaderNote, type ReadResult } from "./readerApi";
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

/** The copy's id and the indices of its files: an `f/<n>` that names none of them is no image. In a bundle, `map[n]`
 * is the copy's file the note's own `f/n` shows. */
const Files = createContext<{ copy: string; indices: ReadonlySet<number>; map?: readonly number[] }>({ copy: "", indices: new Set() });

/** A bundle's notes by id, how to go to one, and whether this is an embedded note (whose embeds stay links). */
const Bundle = createContext<{ notes: ReadonlyMap<number, ReaderNote>; go(id: number, heading: string): void; embedded: boolean } | null>(null);

/** `#kn-3` or `#kn-3/Big%20idea`: a link to note 3 of the copy, at a heading (Publish 2.0). */
export const noteLink = (href: string | undefined) => {
  const m = href === undefined ? null : /^#kn-(\d+)(?:\/(.*))?$/.exec(href);
  if (!m) return null;
  let heading = "";
  try {
    heading = decodeURIComponent(m[2] ?? "");
  } catch {
    heading = "";
  }
  return { id: Number(m[1]), heading };
};

// Only the copy's own files (`f/<n>`): an image from anywhere else would tell a third party who reads, and when.
function CopyImage({ src, alt }: { src?: string; alt?: string }) {
  const files = useContext(Files);
  const local = src === undefined ? undefined : /^f\/(\d+)$/.exec(src)?.[1];
  const index = local === undefined ? undefined : files.map ? files.map[Number(local)] : Number(local);
  return index !== undefined && files.indices.has(index) ? <CopiedImage copy={files.copy} index={index} alt={alt ?? ""} /> : null;
}

/** A link to another note of the copy: it goes there within the page; one to a note that left the copy is its text. */
function NoteLinkTo({ href, children }: { href: string; children: ReactNode }) {
  const bundle = useContext(Bundle);
  const target = noteLink(href);
  if (!bundle || !target || !bundle.notes.has(target.id)) return <>{children}</>;
  return (
    <a
      href={`#/${target.id}`}
      onClick={(e) => {
        e.preventDefault();
        bundle.go(target.id, target.heading);
      }}
      className="text-accent-text underline decoration-accent/45 underline-offset-[3px]"
    >
      {children}
    </a>
  );
}

const fence = /^ {0,3}(`{3,}|~{3,})/;

/**
 * The part of a note an embed names (`![[Plan#Big idea]]`): its heading and what lies under it, to the next heading of
 * the same or a higher level; for a block (`^b1`), the paragraph that ends with it. Code is no heading. Null when the
 * note has no such part.
 */
export function sectionOf(markdown: string, subpath: string): string | null {
  const lines = markdown.split("\n");
  let open: string | null = null;
  const outside = lines.map((line) => {
    const f = fence.exec(line)?.[1];
    if (open === null && f) open = f[0];
    else if (open !== null && f?.[0] === open) {
      open = null;
      return false;
    }
    return open === null;
  });
  if (subpath.startsWith("^")) {
    const id = subpath.slice(1);
    const at = lines.findIndex((line, i) => outside[i] && line.trimEnd().endsWith(` ^${id}`));
    if (at < 0) return null;
    let [from, to] = [at, at];
    while (from > 0 && outside[from - 1] && lines[from - 1].trim()) from--;
    while (to + 1 < lines.length && outside[to + 1] && lines[to + 1].trim()) to++;
    return lines.slice(from, to + 1).join("\n");
  }
  const wanted = subpath.trim().toLowerCase();
  const level = (i: number) => (outside[i] ? (atxHeading(lines[i])?.level ?? 0) : 0);
  const at = lines.findIndex((line, i) => level(i) > 0 && headingText(atxHeading(line)!.text) === wanted);
  if (at < 0) return null;
  let end = at + 1;
  while (end < lines.length && !(level(end) > 0 && level(end) <= level(at))) end++;
  return lines.slice(at, end).join("\n");
}

/** `![x](#kn-3)`: note 3 shown in place, one level deep, or only the part of it the embed names (`#kn-3/Heading`);
 * inside it an embed of a note is a link to it. */
function EmbeddedNote({ src, alt }: { src: string; alt: string }) {
  const bundle = useContext(Bundle);
  const target = noteLink(src);
  const note = target && bundle?.notes.get(target.id);
  const heading = target?.heading;
  const part = useMemo(() => (!note || bundle?.embedded ? null : heading ? sectionOf(note.markdown, heading) : note.markdown), [note, heading, bundle?.embedded]);
  if (!bundle || !note) return <>{alt}</>;
  if (bundle.embedded || part === null) return <NoteLinkTo href={src}>{alt || note.title}</NoteLinkTo>;
  return (
    <Bundle.Provider value={{ ...bundle, embedded: true }}>
      <NoteMarkdown note={{ ...note, markdown: part }} />
    </Bundle.Provider>
  );
}

const remarkPlugins = [remarkGfm, remarkHideBlockIds];

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
  // A `mermaid` block too stays code here: Mermaid sets inline styles in the document while it draws, which this page's
  // CSP (style-src 'self') refuses.
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
  // Only web and mail links; a web link opens in a new tab that learns nothing of this page. A link to another note of
  // the copy goes there. Any other link is its text.
  a: ({ href, children }) =>
    noteLink(href) ? (
      <NoteLinkTo href={href!}>{children}</NoteLinkTo>
    ) : webLink(href) ? (
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
  img: ({ src, alt }) =>
    typeof src === "string" && noteLink(src) ? <EmbeddedNote src={src} alt={alt ?? ""} /> : <CopyImage src={typeof src === "string" ? src : undefined} alt={alt} />,
};

/** One note of a bundle, its files as its own `f/n` name them. */
function NoteMarkdown({ note }: { note: ReaderNote }) {
  const files = useContext(Files);
  return (
    <Files.Provider value={{ ...files, map: note.files }}>
      <ReactMarkdown remarkPlugins={remarkPlugins} skipHtml components={components} urlTransform={keepNoteLinks}>
        {note.markdown}
      </ReactMarkdown>
    </Files.Provider>
  );
}

/** react-markdown's own check, but a link within the copy (`#kn-3`) stays as it is: it is relative, and only read here. */
const keepNoteLinks = (url: string) => (noteLink(url) ? url : defaultUrlTransform(url));

/**
 * The copy as one Markdown text, title first, for pasting into an AI's chat: its fetch tools can't read this page. A
 * bundle's notes each under their own heading, links between them to those headings, as the AI link gives them.
 */
export const copyMarkdown = (copy: ReaderCopy) => {
  if (!copy.notes || copy.notes.length < 2) return `# ${copy.title}\n\n${copy.markdown}`;
  const ids = new Set(copy.notes.map((n) => n.id));
  const relink = (markdown: string) =>
    markdown.replace(/(!?)\[((?:\\.|[^\\\]])*)\]\(#kn-(\d+)(?:\/[^)\s]*)?\)/g, (_, bang: string, text: string, id: string) =>
      // A note that left the copy: its text, with only the brackets unescaped that the copy escaped.
      ids.has(Number(id)) ? `${bang}[${text}](#n${id})` : text.replace(/\\(.)/g, (escape, c: string) => (c === "[" || c === "]" ? c : escape)),
    );
  return copy.notes.map((n) => (n.id === 0 ? `# ${copy.title}\n\n${relink(n.markdown)}` : `\n\n---\n\n# ${n.title} {#n${n.id}}\n\n${relink(n.markdown)}`)).join("");
};

// The page's CSP takes no inline style: the contents are indented by class.
const indent = ["", "pl-3", "pl-6", "pl-9", "pl-12", "pl-15"];

/** The note the address names after the code was read (`#/3`), so a reload stays on it. */
const noteInAddress = () => {
  const m = /^#\/(\d+)$/.exec(location.hash);
  return m ? Number(m[1]) : 0;
};

/** What a copy did, said for a few seconds (the app's `useCopied`, which this page can't import). */
function useSaid() {
  const [said, setSaid] = useState<{ text: string; n: number } | null>(null);
  useEffect(() => {
    if (!said) return;
    const timer = setTimeout(() => setSaid(null), 3000);
    return () => clearTimeout(timer);
  }, [said]);
  const say = (text: string) => setSaid((s) => ({ text, n: (s?.n ?? 0) + 1 }));
  const copyText = (text: string) =>
    navigator.clipboard.writeText(text).then(
      () => say("Copied"),
      () => say("Couldn't copy. Select the text and copy it by hand."),
    );
  const saying = <div aria-live="polite">{said && <span key={said.n} className="text-[14px] text-muted">{said.text}</span>}</div>;
  return { copyText, saying, clear: () => setSaid(null) };
}

type Made = AiLinkResult | { kind: "failed" };

const refusals: Record<Exclude<Made["kind"], "link">, string> = {
  none: "The link couldn't be made: open the note's link again.",
  off: offSentence,
  busy: busySentence,
  failed: "The link couldn't be made; try again later.",
};

/** The link for an AI's fetch tool, a new one with each press, the latest replacing whatever the one before showed. */
function AiLinkMade({ made }: { made: Made | null }) {
  const { copyText, saying, clear } = useSaid();
  const link = made?.kind === "link" ? made.link : undefined;
  // A "Copied" was said of the link before.
  useEffect(() => clear(), [link]);
  return (
    <>
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
      {/* Always laid out: a live region that appears with its text is often not read out. */}
      <p aria-live="polite" className="text-[14px] text-muted">
        {made === null
          ? null
          : made.kind === "link"
            ? `Valid until ${untilText(made.expiresAt)}. Anyone with this link can read this note until then: it is for an AI to fetch, not to post in a chat.`
            : refusals[made.kind]}
      </p>
    </>
  );
}

function Copy({ copy }: { copy: ReaderCopy }) {
  const files = useMemo(() => ({ copy: copy.copy, indices: new Set(copy.files.map((f) => f.index)) }), [copy.copy, copy.files]);
  const markdown = useSaid();
  const [made, setMade] = useState<Made | null>(null);
  const pending = useRef(false);
  const latest = useRef(0);
  const notes = useMemo(() => new Map((copy.notes ?? []).map((n) => [n.id, n])), [copy.notes]);
  const [current, setCurrent] = useState(() => (notes.has(noteInAddress()) ? noteInAddress() : 0));
  const [contents, setContents] = useState(false);
  const [heading, setHeading] = useState("");
  const article = useRef<HTMLElement>(null);
  // Back and forward go between the notes of the copy.
  useEffect(() => {
    if (notes.size === 0) return;
    const back = () => setCurrent(notes.has(noteInAddress()) ? noteInAddress() : 0);
    addEventListener("popstate", back);
    return () => removeEventListener("popstate", back);
  }, [notes]);
  // To the heading a link names, or to the top of the note.
  useEffect(() => {
    const target = heading
      ? [...(article.current?.querySelectorAll("h2, h3, h4, h5, h6") ?? [])].find((h) => h.textContent?.trim() === heading.replace(/^\^.*/, "").trim())
      : null;
    (target ?? article.current)?.scrollIntoView?.({ block: "start" });
  }, [current, heading]);
  const bundle = useMemo(
    () =>
      notes.size === 0
        ? null
        : {
            notes,
            embedded: false,
            go: (id: number, at: string) => {
              history.pushState(null, "", `#/${id}`);
              setContents(false);
              setHeading(at);
              setCurrent(id);
            },
          },
    [notes],
  );
  // Not disabled while it waits, so the focus stays on it; a press meanwhile does nothing.
  const make = () => {
    if (pending.current) return;
    pending.current = true;
    const id = ++latest.current;
    const settle = (next: Made) => {
      pending.current = false;
      if (id === latest.current) setMade(next);
    };
    makeAiLink(copy.copy).then(settle, () => settle({ kind: "failed" }));
  };
  const note = notes.get(current);
  return (
    <article ref={article}>
      <h1 className="text-[28px] leading-tight font-bold text-text">{note && current !== 0 ? note.title : copy.title}</h1>
      <p className="mt-1 mb-4 font-mono text-[12px] text-muted">{snapshotText(copy.snapshotAt)}</p>
      <div className="mb-6 flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-3">
          {bundle && (
            <button type="button" aria-expanded={contents} onClick={() => setContents((c) => !c)} className={buttonClass}>
              Contents
            </button>
          )}
          <button type="button" onClick={() => void markdown.copyText(copyMarkdown(copy))} className={buttonClass}>
            Copy as Markdown
          </button>
          <button type="button" onClick={make} className={buttonClass}>
            Link for your AI
          </button>
          {markdown.saying}
        </div>
        {bundle && contents && (
          <nav aria-label="Contents">
            <ol className="flex flex-col gap-1 rounded-[10px] border border-line bg-surface p-3">
              {[...notes.values()].map((n) => (
                <li key={n.id} className={indent[Math.min(n.depth, 5)]}>
                  <a
                    href={`#/${n.id}`}
                    aria-current={n.id === current ? "page" : undefined}
                    onClick={(e) => {
                      e.preventDefault();
                      bundle.go(n.id, "");
                    }}
                    className="text-accent-text underline decoration-accent/45 underline-offset-[3px] aria-[current=page]:font-bold"
                  >
                    {n.title}
                  </a>
                </li>
              ))}
            </ol>
          </nav>
        )}
        <AiLinkMade made={made} />
      </div>
      <div className="text-[16px] leading-[1.6] break-words text-text-body">
        <Files.Provider value={files}>
          {bundle && note ? (
            <Bundle.Provider value={bundle}>
              <NoteMarkdown note={note} />
            </Bundle.Provider>
          ) : (
            <ReactMarkdown remarkPlugins={remarkPlugins} skipHtml components={components}>
              {copy.markdown}
            </ReactMarkdown>
          )}
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
    const code = location.hash.startsWith("#/") ? "" : location.hash.slice(1);
    if (code) {
      // Out of the address bar, the history and any later copy of the URL before the code goes anywhere.
      history.replaceState(null, "", location.pathname + location.search);
      read(readWithCode(code), true);
    } else {
      read(readAgain(), false);
    }
  }, []);

  // A code pasted into the same tab changes only the hash: it is read as a first load reads it, and the copy shown leaves.
  // The copy's own addresses (`#/3`, `#kn-3…`, `#n3`) are no codes.
  useEffect(() => {
    const on = () => {
      const hash = location.hash;
      if (!hash || /^#\/\d+$/.test(hash) || /^#kn-\d+(\/.*)?$/.test(hash) || /^#n\d+$/.test(hash)) return;
      history.replaceState(null, "", location.pathname + location.search);
      setState({ kind: "loading" });
      setWrong(false);
      read(readWithCode(hash.slice(1)), true);
    };
    addEventListener("hashchange", on);
    return () => removeEventListener("hashchange", on);
  }, []);

  return (
    <main className="mx-auto max-w-[720px] px-5 py-10">
      {state.kind === "loading" && <p className="text-muted">Loading …</p>}
      {state.kind === "copy" && <Copy key={state.copy.copy} copy={state.copy} />}
      {state.kind === "none" && <CodeForm wrong={wrong} pending={pending} onRead={(code) => read(readWithCode(code), true)} />}
      {state.kind === "off" && <p className="text-text-body">{offSentence}</p>}
      {state.kind === "busy" && <p className="text-text-body">{busySentence}</p>}
      {state.kind === "failed" && <p className="text-text-body">The note couldn't be loaded; try again later.</p>}
    </main>
  );
}
