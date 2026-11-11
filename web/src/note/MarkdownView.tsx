// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/user-client/src/components/chat/markdown/MarkdownContent.tsx)
import { Check, ChevronRight, CornerDownRight, Info, Lightbulb, OctagonAlert, Pencil, Quote, TriangleAlert, type LucideIcon } from "lucide-react";
import { createContext, memo, useContext, useEffect, useId, useMemo, useRef, useState, type ComponentPropsWithoutRef, type MouseEvent, type ReactNode, type RefObject } from "react";
import ReactMarkdown, { type Components, type ExtraProps } from "react-markdown";
import { Link } from "react-router-dom";
import remarkGfm from "remark-gfm";
import { useErrorTextFor } from "../discreet/errorText";
import { useLinks, useNote } from "../api/notes";
import { fileKindOf, isAttachment, noteUrl, titleOf, viewUrl, type LightboxState } from "../api/paths";
import { fileKindIcon } from "../components/fileKindIcon";
import TagChip from "../tags/TagChip";
import { remarkTags } from "../tags/tags";
import AmbiguousLinkDialog from "./AmbiguousLinkDialog";
import { remarkCallouts, type CalloutKind } from "./callouts";
import { remarkFind } from "./findInNote";
import { maskComments, remarkMaskedComments } from "./comments";
import { remarkHighlights } from "./highlights";
import { section } from "./outline";
import { prepareMath, remarkRestoreDollars } from "./prepareMath";
import { splitFrontmatter } from "./properties";
import { embedHeading, isExternalUrl, remarkWikiLinks, type LinkView } from "./wikiLinks";

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

interface Props {
  source: string;
  /**
   * The character `source`'s comments are masked with (maskComments, on the whole note before its frontmatter was
   * split off), null if it has none; left out, `source` is read as a whole note and masked here.
   */
  mask?: string | null;
  /** The API's links of this note; until they are in, wiki links show as plain text. */
  links: LinkView[] | undefined;
  /** While a tick is being saved, the next waits: it would be written on top of a version the app doesn't know. */
  tasksDisabled: boolean;
  onToggleTask(line: number): void;
  /** A tap on a link to a note that doesn't exist yet, with its target as written. */
  onCreateMissing(target: string): void;
  /** Agent highlights while "Show" is on, else null. */
  highlight?: Highlight | null;
  /** Find in the note: matches are marked, `current` (0-based) as the current one. */
  find?: { query: string; current: number } | null;
  /** The note shown. With it, notes embedded on a line of their own (`![[plan]]`) show their text; without it
   * (inside an embedded note: one level deep) every embed stays a link. */
  path?: string;
  /** Inside an embedded note: its headings carry no `data-line`, which names lines of the note shown. */
  nested?: boolean;
  /** Inside a checkpoint: where a note or attachment it holds is opened. Its links stay in it, an embed stays a
   * link, and a note it lacks cannot be created. Without it, the note is today's. */
  checkpointLink?: (path: string) => string;
}

// The components below must keep their identity across renders: a new one would remount every block of that
// kind (a tapped checkbox, a focused link). So what changes with the note reaches them through this context.
const NoteContext = createContext<Omit<Props, "source" | "mask" | "highlight" | "find"> & { attachments: string[] }>({
  links: undefined,
  attachments: [],
  tasksDisabled: true,
  onToggleTask: () => {},
  onCreateMissing: () => {},
});

function TaskItem({ node, children, ...rest }: { node: HastNode; children: ReactNode } & ComponentPropsWithoutRef<"li">) {
  const { tasksDisabled, onToggleTask } = useContext(NoteContext);
  const checked = taskChecked(node);
  const line = node.position!.start.line;
  return (
    <li className="k-task my-0.5 flex list-none items-start gap-1" {...rest}>
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

/**
 * An embedded note (`![[plan]]`, `![[plan#Goals]]`): its text (or the heading's section) in a frame, with the way
 * to open it. Its own embeds stay links (one level deep, so no cycle), and its tasks don't tick from here.
 */
function EmbeddedNote({ path, heading }: { path: string; heading: string | null }) {
  const note = useNote(path);
  const errorFor = useErrorTextFor(path);
  const links = useLinks(path, note.data?.version);
  const { onCreateMissing } = useContext(NoteContext);
  const body = useMemo(() => {
    if (!note.data) return null;
    const masked = maskComments(note.data.content);
    const { body } = splitFrontmatter(masked.source);
    const shown = heading === null ? body : section(body, heading);
    return shown === null ? null : { source: shown, mask: masked.mask };
  }, [note.data, heading]);
  const title = heading ? `${titleOf(path)} › ${heading}` : titleOf(path);

  return (
    <section aria-label={`Embedded: ${title}`} className="k-embed my-3 rounded-[10px] border border-line border-l-[3px] border-l-accent/60 bg-surface px-3.5 pt-1 pb-2">
      <Link
        to={noteUrl(path)}
        className="-mx-1 flex min-h-11 items-center gap-1.5 rounded-[6px] px-1 font-mono text-[12px] text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        <CornerDownRight size={14} aria-hidden className="flex-none" />
        <span className="truncate">{title}</span>
        <span className="sr-only">(open the note)</span>
      </Link>
      {note.isPending && <p className="text-sm text-muted">Loading …</p>}
      {note.isError && <p className="text-sm text-muted">{errorFor(note.error)}</p>}
      {note.data && body === null && <p className="text-sm text-muted">This note has no heading “{heading}”.</p>}
      {body !== null && (
        <MarkdownView source={body.source} mask={body.mask} links={links.data} tasksDisabled onToggleTask={() => {}} onCreateMissing={onCreateMissing} nested />
      )}
    </section>
  );
}

const missingClass = "text-muted underline decoration-dim decoration-dashed underline-offset-[3px]";

/**
 * A link or embed to an attachment of the vault (`![[shot.png]]`, `[[report.pdf]]`): a chip with its kind's icon
 * and its name, never the bytes, which open in the lightbox with the note's other attachments to page through. In a
 * checkpoint, they open as the checkpoint held them (`checkpointLink`).
 */
function AttachmentChip({ path }: { path: string }) {
  const { attachments, checkpointLink } = useContext(NoteContext);
  const kind = fileKindOf(path);
  const Icon = fileKindIcon[kind];
  const name = titleOf(path);
  const content = (
    <>
      <Icon size={14} strokeWidth={2} aria-hidden className="flex-none" />
      <span className="min-w-0 truncate">{name}</span>
    </>
  );
  const chip = "inline-flex max-w-full items-center gap-1 rounded-[6px] border border-line bg-surface-2 px-1.5 align-bottom font-mono text-[0.85em]";
  return (
    <Link
      to={checkpointLink ? checkpointLink(path) : viewUrl(path)}
      state={{ paths: attachments } satisfies LightboxState}
      aria-label={`Open the ${kind === "pdf" ? "PDF" : kind === "text" ? "file" : "image"} ${name}`}
      className={`${chip} text-accent-text focus-visible:outline-2 focus-visible:outline-accent`}
    >
      {content}
    </Link>
  );
}

function WikiLink({ index, embed, children }: { index: number; embed: unknown; children: ReactNode }) {
  const { links, onCreateMissing, path, checkpointLink } = useContext(NoteContext);
  const link = links?.[index];
  const [choosing, setChoosing] = useState(false);
  // Only a note, never this one, and only from the note shown (not from inside an embedded one).
  if (!checkpointLink && embed === "block" && link?.kind === "note" && path && link.path && link.path !== path) {
    return <EmbeddedNote path={link.path} heading={embedHeading(link.raw)} />;
  }
  switch (link?.kind) {
    case "attachment":
      // Without a path: a file of a kind the vault doesn't keep (a .docx), named, not opened.
      return link.path ? <AttachmentChip path={link.path} /> : <span className="text-text-body">{children}</span>;
    case undefined:
      return <span>{children}</span>;
    case "note":
      return (
        <Link to={checkpointLink ? checkpointLink(link.path!) : noteUrl(link.path!)} className="text-accent-text underline decoration-accent/45 underline-offset-[3px]">
          {children}
        </Link>
      );
    case "not_found":
      // A checkpoint is read-only, and the note may well exist today: only say it isn't in this one.
      if (checkpointLink) {
        return (
          <span className="text-muted">
            {children}
            <span className="ml-1 rounded-[4px] bg-surface-2 px-1 font-mono text-[11px] text-muted">not in this checkpoint</span>
          </span>
        );
      }
      // A missing image or PDF looks the same, but is no note to create: it comes by upload.
      if (isAttachment(link.target)) {
        return (
          <span className={missingClass}>
            {children}
            <span className="sr-only"> (missing)</span>
          </span>
        );
      }
      return (
        <button
          type="button"
          aria-label={`Create the note “${link.target}”`}
          onClick={() => onCreateMissing(link.target)}
          className={`text-left ${missingClass} focus-visible:outline-2 focus-visible:outline-accent`}
        >
          {children}
        </button>
      );
    case "ambiguous":
      return (
        <>
          <button
            type="button"
            aria-haspopup="dialog"
            onClick={() => setChoosing(true)}
            className="text-left text-text-body underline decoration-dim decoration-dotted underline-offset-[3px] focus-visible:outline-2 focus-visible:outline-accent"
          >
            {children}
            <span className="ml-1 rounded-[4px] bg-surface-2 px-1 font-mono text-[11px] text-muted no-underline">{link.candidates.length} notes</span>
          </button>
          {choosing && (
            <AmbiguousLinkDialog
              target={link.target}
              candidates={link.candidates}
              hrefOf={(p) => (checkpointLink ? checkpointLink(p) : isAttachment(p) ? viewUrl(p) : noteUrl(p))}
              onClose={() => setChoosing(false)}
            />
          )}
        </>
      );
    default:
      // A heading of this note (`self`): named, not opened.
      return <span className="text-text-body">{children}</span>;
  }
}

/** An inline `#tag` (`remarkTags`); in a checkpoint it only names the tag. */
function InlineTag({ tag }: { tag: string }) {
  const { checkpointLink } = useContext(NoteContext);
  return <TagChip tag={tag} still={!!checkpointLink} />;
}

/**
 * Each view's footnote ids start with its own prefix (an embedded note's `fn-1` is not the note's), and a jump looks
 * for its target inside the view's own root.
 */
const FootnoteContext = createContext<{ prefix: string; root: RefObject<HTMLDivElement> }>({ prefix: "user-content-", root: { current: null } });

// The heading of the footnotes always gets this id from remark-rehype, whatever the prefix.
const footnoteLabel = "footnote-label";

/**
 * A link. A footnote's reference and its way back scroll to their target within the note's scroller and leave the
 * address alone: the browser would scroll the window, and the router would see a new location. Other fragment
 * links (`[x](#goals)`) stay as they were.
 */
function Anchor({ href, footnote, children, ...props }: ComponentPropsWithoutRef<"a"> & { footnote: boolean }) {
  const { prefix, root } = useContext(FootnoteContext);
  const jump = (event: MouseEvent<HTMLAnchorElement>) => {
    event.preventDefault();
    // remark-rehype writes a footnote's href and its target's id from the same encoded label.
    const id = href!.slice(1);
    const target = [...(root.current?.querySelectorAll<HTMLElement>("[id]") ?? [])].find((e) => e.id === id);
    if (!target) return;
    target.scrollIntoView({ block: "center" });
    // The focus goes along, so a keyboard or screen reader carries on from there; a definition's item takes it as the h1 does.
    if (!target.matches("a[href], button, [tabindex]")) {
      target.tabIndex = -1;
      target.classList.add("focus:outline-none");
    }
    target.focus({ preventScroll: true });
  };
  const describedBy = props["aria-describedby"] === footnoteLabel ? prefix + footnoteLabel : props["aria-describedby"];
  return (
    <a
      href={href}
      {...props}
      aria-describedby={describedBy}
      {...(isExternalUrl(href) ? { target: "_blank", rel: "noopener noreferrer" } : {})}
      {...(href?.startsWith("#") ? { onClick: jump } : {})}
      className={footnote ? "px-0.5 text-accent-text no-underline" : "text-accent-text underline decoration-accent/45 underline-offset-[3px]"}
    >
      {children}
    </a>
  );
}

/** The footnotes' heading, with the view's own id; other headings have no id. */
function useHeadingId(id: string | undefined) {
  const { prefix } = useContext(FootnoteContext);
  return id === footnoteLabel ? prefix + footnoteLabel : id;
}

/** A heading's source line for jumps and scroll targets; none inside an embedded note, whose lines are its own. */
function useLine(node: HastNode | undefined) {
  const { nested } = useContext(NoteContext);
  return nested ? undefined : node?.position?.start.line;
}

const calloutIcons: Record<CalloutKind, LucideIcon> = { note: Pencil, tip: Lightbulb, info: Info, warning: TriangleAlert, danger: OctagonAlert, quote: Quote };

// Tokens only: the kinds differ by icon and title first; warnings and dangers also take the accent.
const calloutBox: Record<CalloutKind, string> = {
  note: "border-l-dim bg-surface",
  tip: "border-l-dim bg-surface",
  info: "border-l-dim bg-surface",
  warning: "border-l-accent bg-surface",
  danger: "border-l-accent bg-accent/10",
  quote: "border-l-muted bg-surface italic",
};
const calloutClass = (kind: CalloutKind) => `k-callout my-3 rounded-[10px] border border-l-[3px] border-line px-3.5 py-2.5 text-text-body [&>p]:my-1.5 ${calloutBox[kind]}`;
const kindOf = (node: HastNode | undefined, key: "dataCallout" | "dataCalloutTitle") => (node?.properties[key] as CalloutKind | undefined) ?? "note";

function CalloutTitle({ kind, foldable, children }: { kind: CalloutKind; foldable: boolean; children: ReactNode }) {
  const Icon = calloutIcons[kind];
  const accent = kind === "warning" || kind === "danger";
  return (
    <>
      <Icon size={17} strokeWidth={2} aria-hidden className={`flex-none ${accent ? "text-accent-text" : "text-muted"}`} />
      <span className="min-w-0 flex-1 font-semibold not-italic text-text">{children}</span>
      {foldable && <ChevronRight size={16} aria-hidden className="k-fold flex-none text-dim transition-transform" />}
    </>
  );
}


const components: Components = {
  // Obsidian callouts (callouts.ts): a box, foldable ones as <details>.
  aside: ({ node, children, ...props }) =>
    node?.properties.dataCallout ? (
      <aside {...props} className={calloutClass(kindOf(node, "dataCallout"))}>
        {children}
      </aside>
    ) : (
      <aside {...props}>{children}</aside>
    ),
  details: ({ node, children, ...props }) => (
    <details {...props} className={`${calloutClass(kindOf(node, "dataCallout"))} [&[open]_.k-fold]:rotate-90`}>
      {children}
    </details>
  ),
  summary: ({ node, children, ...props }) => (
    <summary {...props} className="-my-1.5 flex min-h-11 cursor-pointer list-none items-center gap-2 rounded-[6px] focus-visible:outline-2 focus-visible:outline-accent [&::-webkit-details-marker]:hidden">
      <CalloutTitle kind={kindOf(node, "dataCalloutTitle")} foldable>
        {children}
      </CalloutTitle>
    </summary>
  ),
  h1: ({ node, ...props }) => <h1 data-line={useLine(node)} tabIndex={-1} className="mt-6 mb-3 text-[28px] leading-tight font-bold text-text first:mt-0 focus:outline-none" {...props} />,
  h2: ({ node, ...props }) => (
    <h2 data-line={useLine(node)} className="mt-7 mb-2.5 font-mono text-[12px] leading-[1.4] font-semibold tracking-[0.08em] text-accent-text uppercase first:mt-0" {...props} id={useHeadingId(props.id)} />
  ),
  h3: ({ node, ...props }) => <h3 data-line={useLine(node)} className="mt-5 mb-2 text-[17px] font-bold text-text" {...props} />,
  h4: ({ node, ...props }) => <h4 data-line={useLine(node)} className="mt-4 mb-1.5 text-[16px] font-bold text-text" {...props} />,
  h5: ({ node, ...props }) => <h5 data-line={useLine(node)} className="mt-4 mb-1.5 text-[15px] font-bold text-text-body" {...props} />,
  h6: ({ node, ...props }) => <h6 data-line={useLine(node)} className="mt-4 mb-1.5 text-[14px] font-semibold text-muted" {...props} />,
  // Find's matches take the accent, `==highlights==` (highlights.ts) a highlighter's yellow. dataFind is a number
  // from 0, so test for presence.
  mark: ({ node, ...props }) =>
    node?.properties.dataFind !== undefined ? (
      <mark className={`rounded-[3px] ${node.properties.dataCurrent ? "bg-accent text-on-accent" : "bg-accent/30 text-text"}`} {...props} />
    ) : (
      <mark className="rounded-[3px] bg-highlight px-0.5 text-text" {...props} />
    ),
  p: ({ node, children, ...props }) =>
    node?.properties.dataCalloutTitle ? (
      <p {...props} className="flex items-center gap-2">
        <CalloutTitle kind={kindOf(node, "dataCalloutTitle")} foldable={false}>
          {children}
        </CalloutTitle>
      </p>
    ) : (
      <p className="my-3" {...props}>
        {children}
      </p>
    ),
  // Only tasks: no bullets. A mix keeps the disc for ordinary items; the tasks (`k-task`) give theirs up and move
  // left so their checkbox stands where a bullet would, with their text close to the ordinary items'.
  ul: ({ node, className, ...props }) => {
    const items = node?.children.filter((c) => c.type === "element" && c.tagName === "li") ?? [];
    const onlyTasks = !!className?.includes("contains-task-list") && items.every((li) => li.type === "element" && isTaskItem(li));
    const mixed = !!className?.includes("contains-task-list") && !onlyTasks;
    return (
      <ul
        className={`my-3 ${onlyTasks ? "list-none pl-1" : "list-disc pl-6"} ${mixed ? "[&>.k-task]:-ml-[22px] [&>.k-task>div]:-ml-3" : ""}`}
        {...props}
      />
    );
  },
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
  // A link within the page (a footnote, its way back, or any other `#` link) scrolls there and leaves the URL alone:
  // a hash in the address bar would leave the router's location, and in a discreet note show the real path again.
  a: ({ node, children, ...props }) => (
    <Anchor footnote={node?.properties.dataFootnoteRef !== undefined || node?.properties.dataFootnoteBackref !== undefined} {...props}>
      {children}
    </Anchor>
  ),
  // The footnotes remark-gfm gathers at the end; their "Footnotes" heading is only for screen readers (sr-only).
  section: ({ node, ...props }) =>
    node?.properties.dataFootnotes !== undefined ? (
      <section {...props} className="mt-8 border-t border-line pt-3 text-[14px] text-muted [&_li]:my-0.5 [&_p]:my-1" />
    ) : (
      <section {...props} />
    ),
  // No image is fetched: a note's `![](url)` would tell a third party who reads it, and when. One of the vault's
  // own (`![](/Screenshots/shot.png)`) is an attachment chip instead (`remarkWikiLinks`).
  img: ({ alt }) => <span className="rounded-[4px] bg-surface-2 px-1.5 font-mono text-[0.85em] text-muted">[image{alt ? `: ${alt}` : ""}]</span>,
  span: ({ node, children, ...props }) => {
    const index = node?.properties.dataWikiLink;
    const tag = node?.properties.dataTag;
    if (typeof tag === "string") return <InlineTag tag={tag} />;
    return typeof index === "number" ? (
      <WikiLink index={index} embed={node?.properties.dataEmbed}>
        {children}
      </WikiLink>
    ) : (
      <span {...props}>{children}</span>
    );
  },
};

type MathPlugins = typeof import("./math");
let mathLoading: Promise<MathPlugins> | null = null;
let mathLoaded: MathPlugins | null = null;

/** KaTeX and its plugins once a note with maths asks for them; until then (and if they fail to load) maths stays text. */
function useMath(wanted: boolean): MathPlugins | null {
  const [math, setMath] = useState(mathLoaded);
  useEffect(() => {
    if (!wanted || math) return;
    let live = true;
    mathLoading ??= import("./math").then((m) => (mathLoaded = m));
    mathLoading.then(
      (m) => live && setMath(m),
      () => (mathLoading = null),
    );
    return () => {
      live = false;
    };
  }, [wanted, math]);
  return wanted ? (math ?? mathLoaded) : null;
}

function MarkdownViewBase({ source: given, mask: givenMask, links, tasksDisabled, onToggleTask, onCreateMissing, highlight = null, find = null, path, nested = false, checkpointLink }: Props) {
  const query = find?.query ?? "";
  const current = find?.current ?? 0;
  // Comments masked as the server's fair copy reads them, so the view hides exactly what the copy leaves out.
  const { source, mask } = useMemo(() => (givenMask === undefined ? maskComments(given) : { source: given, mask: givenMask }), [given, givenMask]);
  const prepared = useMemo(() => prepareMath(source), [source]);
  const math = useMath(prepared.maths);
  const remarkPlugins = useMemo(
    () =>
      [
        remarkGfm,
        // The prepared source keeps the original's offsets and lines, so positions, links and tags hold.
        ...(math ? [math.remarkMath, [remarkRestoreDollars, { original: source }]] : []),
        // Before the tags, which would take the mask for a tag's character.
        [remarkMaskedComments, { mask }],
        remarkCallouts,
        remarkTags,
        [remarkWikiLinks, { links: links ?? [] }],
        remarkHighlights,
        [remarkFind, { query, current }],
        // Before the agent's highlights, which land on the `div` a display gets.
        ...(math ? [math.remarkDisplayParagraph, math.remarkMathBlocks] : []),
        [remarkChanged, { highlight }],
      ] as const,
    [links, highlight, query, current, math, math ? source : null, mask],
  );
  // The note's attachments in the order it names them, each once: what the lightbox pages through from a chip.
  const attachments = useMemo(() => [...new Set((links ?? []).flatMap((l) => (l.kind === "attachment" && l.path ? [l.path] : [])))], [links]);
  const context = useMemo(
    () => ({ links, attachments, tasksDisabled, onToggleTask, onCreateMissing, path, nested, checkpointLink }),
    [links, attachments, tasksDisabled, onToggleTask, onCreateMissing, path, nested, checkpointLink],
  );

  // Every view, an embedded note's and History's too, gets ids of its own: the same note shown twice repeats none.
  // useId's colons (":r0:") are no characters for an id in a URL fragment.
  const prefix = `user-content-${useId().toLowerCase().replace(/[^a-z0-9-]/g, "")}-`;
  const root = useRef<HTMLDivElement>(null);
  const footnotes = useMemo(() => ({ prefix, root }), [prefix]);
  // The back link's arrow in text presentation: no colour emoji.
  const rehypeOptions = useMemo(() => ({ clobberPrefix: prefix, footnoteBackContent: "↩︎" }), [prefix]);

  return (
    <NoteContext.Provider value={context}>
      <FootnoteContext.Provider value={footnotes}>
        <div ref={root} className="text-[16px] leading-[1.6] break-words text-text-body">
          <ReactMarkdown remarkPlugins={remarkPlugins as never} rehypePlugins={math?.rehypePlugins} remarkRehypeOptions={rehypeOptions} components={components}>
            {math ? prepared.source : source}
          </ReactMarkdown>
        </div>
      </FootnoteContext.Provider>
    </NoteContext.Provider>
  );
}

/** A note's Markdown, rendered: GFM, wiki links as the API resolved them, tasks that tick on their source line. */
const MarkdownView = memo(MarkdownViewBase);
export default MarkdownView;
