// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import { useLinks } from "../api/notes";
import { pathFromRoute, titleOf } from "../api/paths";
import { timeText } from "../components/Sheet";
import { useErrorTextFor } from "../discreet/errorText";
import { useDesktop } from "../hooks/useDesktop";
import { ago } from "../note/AgentBanner";
import { maskComments } from "../note/comments";
import MarkdownView from "../note/MarkdownView";
import { Properties } from "../note/NoteView";
import { splitFrontmatter } from "../note/properties";
import WindowFrame from "../windows/WindowFrame";
import { useBlame, type BlameCount, type BlameSection, type BlameView } from "./api";

const noop = () => {};

/** A writer's mark, by her index in the answer's writers; the lines before the record have none of these. */
const marks = ["bg-flag-blue", "bg-flag-purple", "bg-flag-green", "bg-flag-yellow", "bg-flag-red"];
const markOf = (writer: number | null) => (writer === null ? "bg-dim" : marks[writer % marks.length]);

// In the browser's time zone, as History has its times.
const day = (iso: string) => new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short" });
const clock = (iso: string) => new Date(iso).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
const dayAndClock = (iso: string) => `${day(iso)} ${clock(iso)}`;
/** Today's time alone, an earlier one with its day: "14:02", "17 Nov 14:02". */
const shortTime = (iso: string) => (new Date(iso).toDateString() === new Date().toDateString() ? clock(iso) : dayAndClock(iso));

/** The kind as History shows it. */
const kindText = (kind: string | null | undefined) => (kind === "device" ? "Obsidian device" : null);

const asIndex = (writer: number | string | null) => (writer === null ? null : Number(writer));

/**
 * `/blame/:vault/*`, a light window over the notes: who wrote what in a note, section by section in note order (blame
 * spec §7). The server's state, of one version with its text; the note's menu offers it in Read mode only.
 */
export default function BlamePage() {
  const { vault = "", "*": rest = "" } = useParams();
  const path = pathFromRoute(vault, rest);
  const title = titleOf(path);
  const blame = useBlame(path);
  const errorFor = useErrorTextFor(path);
  const desktop = useDesktop();

  return (
    <WindowFrame kind="light" title="Who wrote what" heading={title}>
      <div className="px-3 py-3 lg:px-4">
        <div className="mx-auto flex max-w-2xl flex-col gap-4">
          {desktop && <h3 className="truncate text-xl font-bold text-text">{title}</h3>}
          {blame.isPending && <p className="text-sm text-muted">Loading …</p>}
          {blame.isError && <p className="text-text">{errorFor(blame.error)}</p>}
          {blame.data && <Blame view={blame.data} path={path} desktop={desktop} />}
        </div>
      </div>
    </WindowFrame>
  );
}

function Blame({ view, path, desktop }: { view: BlameView; path: string; desktop: boolean }) {
  const links = useLinks(path, view.version);
  // The comments masked over the whole note, as the note shows it, then cut into its sections' lines.
  const masked = useMemo(() => {
    const { source, mask } = maskComments(view.content);
    return { lines: source.split("\n"), mask, frontmatter: splitFrontmatter(view.content).frontmatter };
  }, [view.content]);

  if (view.sections.length === 0) return <p className="text-text-body">This note is empty: no one has written in it yet.</p>;
  return (
    <>
      <Legend view={view} />
      <ol aria-label="Sections" className="flex flex-col gap-5">
        {view.sections.map((section) => {
          const start = Number(section.start) - 1;
          const end = Number(section.end);
          return (
            <li key={start} data-section className={desktop ? "grid grid-cols-[9.5rem_minmax(0,1fr)] gap-4" : "flex flex-col gap-1"}>
              <Gutter section={section} view={view} />
              <div className="min-w-0">
                {start === 0 && masked.frontmatter !== null ? (
                  <Properties yaml={masked.frontmatter} changed={null} still />
                ) : (
                  <MarkdownView source={masked.lines.slice(start, end).join("\n")} mask={masked.mask} links={links.data} tasksDisabled onToggleTask={noop} tagsStill />
                )}
              </div>
            </li>
          );
        })}
      </ol>
    </>
  );
}

/** Sections by their main writer, in the order they first lead one; the lines before the record last. */
function Legend({ view }: { view: BlameView }) {
  const counts = new Map<number | null, number>();
  for (const s of view.sections) if (s.writer !== null) counts.set(asIndex(s.writer), (counts.get(asIndex(s.writer)) ?? 0) + 1);
  const before = view.sections.filter((s) => s.writer === null).length;
  if (before > 0) counts.set(null, before);
  const items = [...counts].map(([writer, n], i) => {
    const noun = i === 0 ? (n === 1 ? " section" : " sections") : "";
    if (writer === null) return { writer, text: `${n}${noun} from before ${day(view.earlier!)}` };
    const kind = kindText(view.writers[writer]?.kind);
    return { writer, text: `${n}${noun} by ${view.writers[writer]?.display}${kind ? ` · ${kind}` : ""}` };
  });

  return (
    <div className="flex flex-col gap-2">
      <ul aria-label="Writers" className="flex flex-wrap gap-x-4 gap-y-1 text-[14px] text-text-body">
        {items.map((item) => (
          <li key={item.writer ?? "before"} className="flex items-center gap-1.5">
            <Mark writer={item.writer} />
            {item.text}
          </li>
        ))}
      </ul>
      {view.earlier && (
        <p className="text-[13px] text-muted">
          {view.dropped
            ? `The record of who wrote each line started again at ${dayAndClock(view.earlier)}, after more than 4,096 changes of writer; lines from before are older than that.`
            : `Kumbu² records who wrote each line from version 0.6.0 on; lines from before ${dayAndClock(view.earlier)} are older than what the note's history keeps.`}
        </p>
      )}
    </div>
  );
}

const Mark = ({ writer }: { writer: number | null }) => <span data-mark aria-hidden="true" className={`size-2.5 flex-none rounded-full ${markOf(writer)}`} />;

/** A time shown short, the full one on hover and for screen readers. */
function When({ iso, text }: { iso: string; text: string }) {
  return (
    <time dateTime={iso} title={timeText(iso)} className="whitespace-nowrap">
      <span aria-hidden="true">{text}</span>
      <span className="sr-only">{timeText(iso)}</span>
    </time>
  );
}

/** The section's main writer, her kind and its newest recorded line; activated, every writer of it, in place. */
function Gutter({ section, view }: { section: BlameSection; view: BlameView }) {
  const [open, setOpen] = useState(false);
  const detailId = useId();
  const main = asIndex(section.writer);
  const name = main === null ? `Before ${day(view.earlier!)}` : view.writers[main]?.display;
  const kind = main === null ? null : kindText(view.writers[main]?.kind);
  const recorded = section.counts.filter((c) => c.writer !== null).map((c) => c.at);
  const newest = recorded.length ? recorded.reduce((a, b) => (Date.parse(b) > Date.parse(a) ? b : a)) : null;

  return (
    <div className="flex min-w-0 flex-col gap-1">
      <button
        type="button"
        aria-expanded={open}
        aria-controls={open ? detailId : undefined}
        onClick={() => setOpen(!open)}
        className="flex min-h-12 flex-col items-start justify-center rounded-[10px] px-2 py-1.5 text-left text-[13px] text-muted hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-accent"
      >
        <span className="flex items-center gap-1.5 text-[14px] font-medium text-text">
          <Mark writer={main} />
          {name}
          {section.others && " and others"}
        </span>
        {kind && <span>{kind}</span>}
        {newest && <When iso={newest} text={ago(newest)} />}
      </button>
      {open && (
        <ul id={detailId} aria-label={`Writers of ${section.title}`} className="flex flex-col gap-1 px-2 text-[13px] text-text-body">
          {section.counts.map((count, i) => (
            <Count key={i} count={count} view={view} />
          ))}
        </ul>
      )}
    </div>
  );
}

function Count({ count, view }: { count: BlameCount; view: BlameView }) {
  const writer = asIndex(count.writer);
  const lines = Number(count.lines);
  const many = `${lines} ${lines === 1 ? "line" : "lines"}`;
  if (writer === null) {
    return (
      <li className="flex items-baseline gap-1.5">
        <Mark writer={null} />
        <span>
          Before {dayAndClock(count.at)}, {many}
        </span>
      </li>
    );
  }
  const kind = kindText(view.writers[writer]?.kind);
  return (
    <li className="flex items-baseline gap-1.5">
      <Mark writer={writer} />
      <span>
        {count.display}
        {kind && ` · ${kind}`}, {many}, <When iso={count.at} text={shortTime(count.at)} />
      </span>
    </li>
  );
}
