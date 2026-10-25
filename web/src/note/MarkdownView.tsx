// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/user-client/src/components/chat/markdown/MarkdownContent.tsx)
import { Check } from "lucide-react";
import { createContext, memo, useContext, useMemo, type ComponentPropsWithoutRef, type ReactNode } from "react";
import ReactMarkdown, { type Components, type ExtraProps } from "react-markdown";
import { Link } from "react-router-dom";
import remarkGfm from "remark-gfm";
import { noteUrl } from "../api/paths";
import { remarkWikiLinks, type LinkView } from "./wikiLinks";

/** The blocks an agent changed: their 1-based source lines (or all of them), and the key that wrote them. */
export interface Highlight {
  lines: Set<number> | "all";
  writer: string;
}

interface MdBlock {
  type: string;
  children?: MdBlock[];
  position?: { start: { line: number }; end: { line: number } };
  data?: { hProperties?: Record<string, unknown> };
}

/** Marks each block (each item, in a list) with a changed source line; index.css tints them and names the writer. */
function remarkChanged({ highlight }: { highlight: Highlight | null }) {
  return (tree: MdBlock) => {
    if (!highlight) return;
    const touched = (node: MdBlock) => {
      if (highlight.lines === "all") return true;
      const { start, end } = node.position ?? { start: { line: 0 }, end: { line: -1 } };
      for (let line = start.line; line <= end.line; line++) if (highlight.lines.has(line)) return true;
      return false;
    };
    for (const block of tree.children ?? []) {
      for (const node of block.type === "list" ? (block.children ?? []) : [block]) {
        if (touched(node)) node.data = { ...node.data, hProperties: { ...node.data?.hProperties, dataChanged: true, dataWriter: highlight.writer } };
      }
    }
  };
}

type HastNode = NonNullable<ExtraProps["node"]>;
type HastChild = HastNode["children"][number];

/** The text of a list item without its nested lists: a task's accessible name. */
function textOf(node: HastChild): string {
  if (node.type === "text") return node.value;
  if (node.type !== "element" || node.tagName === "ul" || node.tagName === "ol") return "";
  return node.children.map(textOf).join("");
}

function isTaskItem(node: HastNode | undefined) {
  const className = node?.properties.className;
  return Array.isArray(className) && className.includes("task-list-item");
}

/** The item's checkbox as remark-gfm made it: directly in the item (tight list) or in its first paragraph. */
function taskChecked(node: HastNode): boolean {
  const input = (n: HastChild): HastChild | undefined =>
    n.type !== "element" ? undefined : n.tagName === "input" ? n : n.tagName === "p" ? n.children.map(input).find(Boolean) : undefined;
  const found = node.children.map(input).find(Boolean);
  return found?.type === "element" && !!found.properties.checked;
}

const external = (href: string | undefined) => !!href && /^(https?:|mailto:)/i.test(href);

interface Props {
  source: string;
  /** The API's links of this note; until they are in, wiki links show as plain text. */
  links: LinkView[] | undefined;
  /** While a tick is being saved, the next waits: it would be written on top of a version the app doesn't know. */
  tasksDisabled: boolean;
  onToggleTask(line: number): void;
  /** A tap on a link to a note that doesn't exist yet, with its target as written. */
  onCreateMissing(target: string): void;
  /** Agent highlights while "Show" is on, else null. */
  highlight?: Highlight | null;
}

// The components below must keep their identity across renders: a new one would remount every block of that
// kind (a tapped checkbox, a focused link). So what changes with the note reaches them through this context.
const NoteContext = createContext<Omit<Props, "source" | "highlight">>({ links: undefined, tasksDisabled: true, onToggleTask: () => {}, onCreateMissing: () => {} });

function TaskItem({ node, children, ...rest }: { node: HastNode; children: ReactNode } & ComponentPropsWithoutRef<"li">) {
  const { tasksDisabled, onToggleTask } = useContext(NoteContext);
  const checked = taskChecked(node);
  const line = node.position!.start.line;
  return (
    <li className="my-0.5 flex items-start gap-1" {...rest}>
      <button
        type="button"
        role="checkbox"
        aria-checked={checked}
        aria-label={node.children.map(textOf).join("").trim()}
        disabled={tasksDisabled}
        onClick={() => onToggleTask(line)}
        className="-my-2.5 -ml-2.5 flex size-11 flex-none items-center justify-center rounded-[8px] focus-visible:outline-2 focus-visible:outline-accent disabled:opacity-60"
      >
        <span className={`flex size-5 items-center justify-center rounded-[5px] border-[1.5px] ${checked ? "border-accent bg-accent text-on-accent" : "border-dim"}`}>
          {checked && <Check size={14} strokeWidth={3} aria-hidden />}
        </span>
      </button>
      <div className={`min-w-0 flex-1 [&>p]:my-0 ${checked ? "text-muted line-through decoration-dim" : ""}`}>{children}</div>
    </li>
  );
}

function WikiLink({ index, children }: { index: number; children: ReactNode }) {
  const { links, onCreateMissing } = useContext(NoteContext);
  const link = links?.[index];
  switch (link?.kind) {
    case undefined:
      return <span>{children}</span>;
    case "note":
      return (
        <Link to={noteUrl(link.path!)} className="text-accent-text underline decoration-accent/45 underline-offset-[3px]">
          {children}
        </Link>
      );
    case "not_found":
      return (
        <button
          type="button"
          aria-label={`Create the note “${link.target}”`}
          onClick={() => onCreateMissing(link.target)}
          className="text-left text-muted underline decoration-dim decoration-dashed underline-offset-[3px] focus-visible:outline-2 focus-visible:outline-accent"
        >
          {children}
        </button>
      );
    case "ambiguous":
      return (
        <span title={`Several notes match: ${link.candidates.join(", ")}`} className="text-text-body underline decoration-dim decoration-dotted underline-offset-[3px]">
          {children}
          <span className="ml-1 rounded-[4px] bg-surface-2 px-1 font-mono text-[11px] text-muted no-underline">{link.candidates.length} notes</span>
        </span>
      );
    default:
      // A heading of this note (`self`) or an attachment: named, not opened.
      return <span className="text-text-body">{children}</span>;
  }
}

const components: Components = {
  h1: ({ node: _, ...props }) => <h1 className="mt-6 mb-3 text-[28px] leading-tight font-bold text-text first:mt-0" {...props} />,
  h2: ({ node: _, ...props }) => (
    <h2 className="mt-7 mb-2.5 font-mono text-[12px] leading-[1.4] font-semibold tracking-[0.08em] text-accent-text uppercase first:mt-0" {...props} />
  ),
  h3: ({ node: _, ...props }) => <h3 className="mt-5 mb-2 text-[17px] font-bold text-text" {...props} />,
  p: ({ node: _, ...props }) => <p className="my-3" {...props} />,
  ul: ({ node: _, className, ...props }) => (
    <ul className={`my-3 ${className?.includes("contains-task-list") ? "list-none pl-1" : "list-disc pl-6"}`} {...props} />
  ),
  ol: ({ node: _, ...props }) => <ol className="my-3 list-decimal pl-6" {...props} />,
  li: ({ node, children, className: __, ...props }) =>
    node && isTaskItem(node) ? (
      <TaskItem node={node} {...props}>
        {children}
      </TaskItem>
    ) : <li className="my-1 marker:text-dim" {...props}>{children}</li>,
  // The task's checkbox is the button of its item, above.
  input: ({ node: _, type, ...props }) => (type === "checkbox" ? null : <input type={type} {...props} />),
  blockquote: ({ node: _, ...props }) => <blockquote className="my-3 border-l-[3px] border-accent pl-3.5 text-text-body [&>p]:my-1" {...props} />,
  pre: ({ node: _, ...props }) => (
    <pre className="my-3 overflow-x-auto rounded-[10px] border border-line bg-surface px-3.5 py-3 font-mono text-[12.5px] leading-[1.6] text-text-body [&>code]:bg-transparent [&>code]:p-0" {...props} />
  ),
  code: ({ node: _, ...props }) => <code className="rounded-[4px] bg-surface-2 px-[5px] py-px font-mono text-[0.85em]" {...props} />,
  strong: ({ node: _, ...props }) => <strong className="font-bold text-text" {...props} />,
  hr: () => <hr className="my-6 border-line" />,
  table: ({ node: _, ...props }) => (
    <div className="my-3 overflow-x-auto">
      <table className="border-collapse text-[15px] [&_td]:border [&_td]:border-line [&_td]:px-2 [&_td]:py-1 [&_th]:border [&_th]:border-line [&_th]:px-2 [&_th]:py-1 [&_th]:text-left" {...props} />
    </div>
  ),
  // react-markdown's default urlTransform already drops dangerous protocols (javascript: and the like).
  a: ({ node: _, href, children, ...props }) => (
    <a
      href={href}
      {...props}
      {...(external(href) ? { target: "_blank", rel: "noopener noreferrer" } : {})}
      className="text-accent-text underline decoration-accent/45 underline-offset-[3px]"
    >
      {children}
    </a>
  ),
  // No image is fetched: a note's `![](url)` would tell a third party who reads it, and when (text first in v1).
  img: ({ alt }) => <span className="rounded-[4px] bg-surface-2 px-1.5 font-mono text-[0.85em] text-muted">[image{alt ? `: ${alt}` : ""}]</span>,
  span: ({ node, children, ...props }) => {
    const index = node?.properties.dataWikiLink;
    return typeof index === "number" ? <WikiLink index={index}>{children}</WikiLink> : <span {...props}>{children}</span>;
  },
};

function MarkdownViewBase({ source, links, tasksDisabled, onToggleTask, onCreateMissing, highlight = null }: Props) {
  const remarkPlugins = useMemo(
    () => [remarkGfm, [remarkWikiLinks, { links: links ?? [] }], [remarkChanged, { highlight }]] as const,
    [links, highlight],
  );
  const context = useMemo(() => ({ links, tasksDisabled, onToggleTask, onCreateMissing }), [links, tasksDisabled, onToggleTask, onCreateMissing]);

  return (
    <NoteContext.Provider value={context}>
      <div className="text-[16px] leading-[1.6] break-words text-text-body">
        <ReactMarkdown remarkPlugins={remarkPlugins as never} components={components}>
          {source}
        </ReactMarkdown>
      </div>
    </NoteContext.Provider>
  );
}

/** A note's Markdown, rendered: GFM, wiki links as the API resolved them, tasks that tick on their source line. */
const MarkdownView = memo(MarkdownViewBase);
export default MarkdownView;
