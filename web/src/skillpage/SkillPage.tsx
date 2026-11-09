// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef, useState } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { openAgain, openWithCode, zip, type OpenResult, type SharedSkill } from "./skillApi";

type State = { kind: "loading" } | { kind: "failed" } | OpenResult;

const webLink = (href: string | undefined) => {
  try {
    return !!href && ["http:", "https:", "mailto:"].includes(new URL(href).protocol);
  } catch {
    return false;
  }
};

/** "Snapshot of 5 Nov 2026, 14:32", in the visitor's own time zone. */
function snapshotText(iso: string): string {
  const parts = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(
    new Date(iso),
  );
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? "";
  return `Snapshot of ${part("day")} ${part("month")} ${part("year")}, ${part("hour")}:${part("minute")}`;
}

const sizeText = (bytes: number) =>
  bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`;

const remarkPlugins = [remarkGfm];

// The folder's name is the page's h1: the manifest's headings go one level down.
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
        rel="noreferrer noopener"
        className="text-accent-text underline decoration-accent/45 underline-offset-[3px]"
      >
        {children}
      </a>
    ) : (
      <>{children}</>
    ),
  // Never fetched: an image from anywhere would tell a third party who reads, and when; the skill's own files are
  // in the .zip.
  img: ({ alt }) => (alt ? <span className="font-mono text-[0.85em] text-muted">{alt}</span> : null),
};

function Download({ skill }: { skill: SharedSkill }) {
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);
  // The last download's URL and its revoke timer: the page going revokes it at once.
  const saved = useRef<{ url: string; timer: ReturnType<typeof setTimeout> }>(undefined);
  useEffect(
    () => () => {
      if (!saved.current) return;
      clearTimeout(saved.current.timer);
      URL.revokeObjectURL(saved.current.url);
    },
    [],
  );
  const download = async () => {
    setPending(true);
    setFailed(false);
    try {
      const url = URL.createObjectURL(await zip(skill.copy));
      const link = document.createElement("a");
      link.href = url;
      link.download = `${skill.name}.zip`;
      // In the document: Firefox ignores a click on a link outside it.
      document.body.append(link);
      try {
        link.click();
      } finally {
        link.remove();
      }
      if (saved.current) {
        clearTimeout(saved.current.timer);
        URL.revokeObjectURL(saved.current.url);
      }
      // Not in the same tick: Safari can lose the download when the URL goes before it started.
      const timer = setTimeout(() => {
        URL.revokeObjectURL(url);
        if (saved.current?.url === url) saved.current = undefined;
      }, 1000);
      saved.current = { url, timer };
    } catch {
      setFailed(true);
    } finally {
      setPending(false);
    }
  };
  return (
    <div className="flex flex-col gap-1.5">
      <button
        type="button"
        onClick={() => void download()}
        disabled={pending}
        className="min-h-11 self-start rounded-[8px] bg-accent px-4 font-medium text-on-accent focus-visible:outline-2 focus-visible:outline-accent disabled:opacity-60"
      >
        Download as .zip
      </button>
      {failed && (
        <p role="alert" className="text-[14px] text-warning">
          The .zip couldn't be downloaded; open the link again and retry.
        </p>
      )}
    </div>
  );
}

function Skill({ skill, code }: { skill: SharedSkill; code: string | undefined }) {
  return (
    <article>
      <h1 className="text-[28px] leading-tight font-bold text-text">{skill.name}</h1>
      {skill.description && <p className="mt-2 text-[16px] text-text-body">{skill.description}</p>}
      <p className="mt-1 mb-6 font-mono text-[12px] text-muted">{snapshotText(skill.snapshotAt)}</p>

      {skill.hasScripts && (
        <p role="note" className="mb-6 rounded-[8px] border border-warning px-3.5 py-2.5 text-[15px] text-text">
          This skill contains scripts. Kumbu² never runs them; a harness you give the skill to may.
        </p>
      )}

      <div className="mb-8 flex flex-wrap items-start gap-x-6 gap-y-4">
        <Download skill={skill} />
        {code && (
          <div className="flex flex-col gap-1.5">
            {/* A plain link: the app takes over there, with its session; this page makes no signed-in call. */}
            <a
              href={`/skills/add#${code}`}
              className="flex min-h-11 items-center self-start rounded-[8px] border border-line px-4 font-medium text-text focus-visible:outline-2 focus-visible:outline-accent"
            >
              Add to my skills
            </a>
            <p className="text-[13px] text-muted">Needs an account on this server.</p>
          </div>
        )}
      </div>

      <div className="text-[16px] leading-[1.6] break-words text-text-body">
        <ReactMarkdown remarkPlugins={remarkPlugins} skipHtml components={components}>
          {skill.manifest}
        </ReactMarkdown>
      </div>

      <h2 id="skill-files" className="mt-8 mb-2 text-[17px] font-bold text-text">
        Files
      </h2>
      <ul aria-labelledby="skill-files" className="divide-y divide-line rounded-[8px] border border-line">
        {skill.files.map((f) => (
          <li key={f.index} className="flex justify-between gap-4 px-3.5 py-2 text-[14px]">
            <span className="font-mono break-all text-text">{f.path}</span>
            <span className="shrink-0 text-muted">{sizeText(f.size)}</span>
          </li>
        ))}
      </ul>
    </article>
  );
}

export default function SkillPage() {
  const [state, setState] = useState<State>({ kind: "loading" });
  // Only while this page holds it, and kept nowhere else: a reload opens the skill by its cookie alone, so the code,
  // and with it "Add to my skills", is gone then. Storing it would leave the code in this origin's storage.
  const [code, setCode] = useState<string>();
  const started = useRef(false);

  useEffect(() => {
    // Once, even when React runs effects twice: the second run would find the fragment gone.
    if (started.current) return;
    started.current = true;
    const typed = location.hash.slice(1);
    let request: Promise<OpenResult>;
    if (typed) {
      // Out of the address bar, the history and any later copy of the URL before the code goes anywhere.
      history.replaceState(null, "", location.pathname + location.search);
      request = openWithCode(typed);
    } else {
      request = openAgain();
    }
    request.then(
      (result) => {
        if (typed && result.kind === "skill") setCode(typed);
        setState(result);
      },
      () => setState({ kind: "failed" }),
    );
  }, []);

  return (
    <main className="mx-auto max-w-[720px] px-5 py-10">
      {state.kind === "loading" && <p className="text-muted">Loading …</p>}
      {state.kind === "skill" && <Skill skill={state.skill} code={code} />}
      {state.kind === "none" && <p className="text-text-body">This link doesn't open a shared skill: the code is wrong, or the skill is no longer shared.</p>}
      {state.kind === "off" && <p className="text-text-body">Shared skills can't be opened on this server.</p>}
      {state.kind === "busy" && <p className="text-text-body">Too many tries; wait a minute and try again.</p>}
      {state.kind === "failed" && <p className="text-text-body">The skill couldn't be loaded; try again later.</p>}
    </main>
  );
}
