// SPDX-License-Identifier: AGPL-3.0-or-later
import { CalendarDays, Eye, Ghost, List, Maximize, Paperclip, Sparkles, type LucideIcon } from "lucide-react";
import { useCallback, useId, useMemo, useRef, useState, type FocusEvent, type MouseEvent, type PointerEvent } from "react";
import { useNavigate } from "react-router-dom";
import { HttpError } from "../api/client";
import { useTree } from "../api/notes";
import { vaultOf, viewUrl } from "../api/paths";
import { useErrorTextFor } from "../discreet/errorText";
import { useOpenNote } from "../shell/openInPane";
import { useGraphPaths, useLocalGraph, type GraphFilters, type GraphPathsView } from "./api";
import GraphCanvas from "./GraphCanvas";
import { toModel, type GNode, type SizeBy } from "./model";
import NodePreview from "./NodePreview";
import { similarity } from "./similarity";
import SuggestionCard from "./SuggestionCard";
import { suggestedLinks, type Suggestion } from "./suggestions";
import { useGraphPrefs, type GraphPrefs } from "./useGraphPrefs";

/** The column's id, for its toggle in the top bar. */
export const graphColumnId = "graph-column";

const sizes: { id: SizeBy; label: string }[] = [
  { id: "links", label: "Links" },
  { id: "backlinks", label: "Backlinks" },
  { id: "length", label: "Length" },
];

const toggles: { pref: "days" | "attachments" | "ghosts" | "preview" | "suggestions"; label: string; icon: LucideIcon }[] = [
  { pref: "days", label: "Day notes", icon: CalendarDays },
  { pref: "attachments", label: "Attachments", icon: Paperclip },
  { pref: "ghosts", label: "Ghosts", icon: Ghost },
  { pref: "preview", label: "Preview", icon: Eye },
  { pref: "suggestions", label: "Suggestions", icon: Sparkles },
];

const tool =
  "flex size-8 flex-none items-center justify-center rounded-md text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent aria-pressed:bg-surface-2 aria-pressed:text-text aria-expanded:bg-surface-2 aria-expanded:text-text";

/** The foot's line on the other pane's note: how many links away, or why it can't say. */
function otherLine(other: GraphPathsView | "other-vault" | null | undefined): string | null {
  if (!other) return null;
  if (other === "other-vault") return "Other pane: in another vault";
  if (other.why === "not_connected") return "Other pane: not connected";
  if (other.why === "farther_than_limit") return "Other pane: more than 6 steps away";
  const steps = other.paths[0]?.length;
  if (steps === undefined) return null;
  return steps === 1 ? "Other pane: 1 step away" : `Other pane: ${steps} steps away`;
}

/**
 * Beside the note on the widest windows: the local graph of `path` (the focused pane's note) with its toolbar, the
 * choices remembered per browser, and a foot line on the similarity and on `otherPath`, the other pane's note. The
 * simulation runs only while the column is shown, not while `hidden`.
 */
export default function GraphColumn({ path, otherPath, hidden }: { path: string; otherPath: string | null; hidden: boolean }) {
  const id = useId();
  const [prefs, change] = useGraphPrefs();
  // The filters alone, so the size or a layer switched never asks the server again.
  const { depth, days, attachments, ghosts } = prefs;
  const filters = useMemo<GraphFilters>(() => ({ depth, days, attachments, ghosts }), [depth, days, attachments, ghosts]);
  const local = useLocalGraph(path, filters, !hidden);
  const tree = useTree(vaultOf(path));
  const errorFor = useErrorTextFor(path);
  const other = useGraphPaths(path, otherPath, filters, !hidden);
  const [fitSignal, setFitSignal] = useState(0);
  const openNote = useOpenNote();
  const navigate = useNavigate();

  // The last graph stands in while the next is asked for only if the note is in it; else it would show another note's.
  const stale = local.isPlaceholderData && !local.data?.nodes.some((n) => n.path === path);
  // Memoised per answer, or the layout would warm again on every render.
  const model = useMemo(() => (local.data && !stale ? toModel(local.data, tree.data) : null), [local.data, stale, tree.data]);
  const unmeasured = useMemo(() => {
    if (!model) return false;
    const keys = new Map(model.nodes.map((n) => [n.id, n.key]));
    return model.edges.some((e) => !similarity(keys.get(e.source)!, keys.get(e.target)!).measured);
  }, [model]);
  const neighbours = useMemo(
    () => (model?.nodes ?? []).filter((n) => n.depth > 0).sort((a, b) => a.depth - b.depth || a.title.localeCompare(b.title)),
    [model],
  );

  // An attachment opens in its window either way: panes hold notes only.
  const open = (n: GNode, beside: boolean) => {
    if (!n.path) return;
    if (n.kind === "attachment") navigate(viewUrl(n.path), { state: { paths: [n.path] } });
    else openNote(n.path, beside ? { side: "right" } : undefined);
  };
  const clicked = (n: GNode) => (e: MouseEvent) => open(n, e.ctrlKey || e.metaKey);

  const otherText = otherLine(other.data);

  const suggestions = useMemo(
    () => (prefs.suggestions && model ? suggestedLinks(path, new Set(model.nodes.flatMap((n) => (n.path ? [n.path] : []))), tree.data) : []),
    [prefs.suggestions, model, path, tree.data],
  );

  // Where the card goes: beside the pointer, or the node that has the keyboard's focus, in the body's coordinates.
  const body = useRef<HTMLDivElement>(null);
  const spot = useRef({ x: 0, y: 0 });
  const at = (clientX: number, clientY: number) => {
    const box = body.current!.getBoundingClientRect();
    spot.current = { x: clientX - box.left, y: Math.min(clientY - box.top, box.height - 160) };
  };
  const pointed = (e: PointerEvent) => at(e.clientX, e.clientY);
  const focused = (e: FocusEvent) => {
    const r = e.target.getBoundingClientRect();
    at(r.right, r.top + r.height / 2);
  };
  // Each kept with the note it was opened on, so neither outlives a new centre.
  const [hover, setHover] = useState<{ node: GNode; anchor: { x: number; y: number }; on: string } | null>(null);
  // With the preview off a hover only clears, so a card left over can't come back when it is switched on again.
  const hovered = (n: GNode | null) => setHover(n && prefs.preview ? { node: n, anchor: spot.current, on: path } : null);
  const [suggestion, setSuggestion] = useState<{ s: Suggestion; on: string } | null>(null);
  const closeSuggestion = useCallback(() => setSuggestion(null), []);

  return (
    <aside id={graphColumnId} aria-label="Graph" hidden={hidden} className="flex w-[560px] flex-none flex-col border-l border-line bg-surface">
      <div role="toolbar" aria-label="Graph options" className="flex flex-none items-center gap-1 border-b border-line px-3 py-2">
        <div role="group" aria-label="Depth" className="flex flex-none overflow-hidden rounded-md border border-line">
          {([1, 2, 3] as const).map((d) => (
            <button
              key={d}
              type="button"
              aria-label={`Depth ${d}`}
              title={`Depth ${d}`}
              aria-pressed={depth === d}
              onClick={() => change({ depth: d })}
              className="flex h-8 w-7 items-center justify-center font-mono text-xs text-muted focus-visible:outline-2 focus-visible:outline-accent aria-pressed:bg-surface-2 aria-pressed:font-medium aria-pressed:text-text"
            >
              {d}
            </button>
          ))}
        </div>
        {toggles.map(({ pref, label, icon: Icon }) => (
          <button key={pref} type="button" aria-label={label} title={label} aria-pressed={prefs[pref]} onClick={() => change({ [pref]: !prefs[pref] } as Partial<GraphPrefs>)} className={tool}>
            <Icon size={16} strokeWidth={1.8} aria-hidden />
          </button>
        ))}
        <select
          aria-label="Size by"
          title="Size by"
          value={prefs.sizeBy}
          onChange={(e) => change({ sizeBy: e.target.value as SizeBy })}
          className="h-8 min-w-0 flex-none rounded-md border border-line bg-surface-2 px-1.5 text-xs text-text focus-visible:outline-2 focus-visible:outline-accent"
        >
          {sizes.map((s) => (
            <option key={s.id} value={s.id}>
              {s.label}
            </option>
          ))}
        </select>
        <span className="flex-1" />
        <button type="button" aria-label="List" title="List" aria-expanded={prefs.list} aria-controls={`${id}-list`} onClick={() => change({ list: !prefs.list })} className={tool}>
          <List size={16} strokeWidth={1.8} aria-hidden />
        </button>
        <button type="button" aria-label="Fit" title="Fit" onClick={() => setFitSignal((n) => n + 1)} className={tool}>
          <Maximize size={16} strokeWidth={1.8} aria-hidden />
        </button>
      </div>
      <div ref={body} onPointerMove={pointed} onFocus={focused} className="relative flex min-h-0 flex-1 flex-col">
        {local.isError && !model ? (
          <div className="m-auto flex flex-col items-center gap-3 px-6 text-center text-sm text-muted">
            <p>{local.error instanceof HttpError && local.error.message ? errorFor(local.error) : "Couldn't draw the graph."}</p>
            <button
              type="button"
              onClick={() => local.refetch()}
              className="min-h-11 rounded-[10px] border border-line bg-surface-2 px-4 text-text focus-visible:outline-2 focus-visible:outline-accent"
            >
              Try again
            </button>
          </div>
        ) : model ? (
          <GraphCanvas
            nodes={model.nodes}
            edges={model.edges}
            centreKey={path}
            otherKey={otherPath}
            sizeBy={prefs.sizeBy}
            suggestions={suggestions}
            showSuggestions={prefs.suggestions}
            onOpen={open}
            onHover={hovered}
            onSuggestion={(s) => setSuggestion({ s, on: path })}
            fitSignal={fitSignal}
            running={!hidden}
          />
        ) : (
          <p className="m-auto text-sm text-muted">Drawing the graph…</p>
        )}
        {model && prefs.preview && hover?.on === path && <NodePreview node={hover.node} anchor={hover.anchor} />}
        {model && suggestion?.on === path && <SuggestionCard suggestion={suggestion.s} onClose={closeSuggestion} />}
      </div>
      {prefs.list && model && (
        <div id={`${id}-list`} className="max-h-[40%] flex-none overflow-y-auto border-t border-line px-[18px] py-2">
          {neighbours.length === 0 ? (
            <p className="py-2 text-sm text-muted">No links yet</p>
          ) : (
            <ul aria-label="Neighbours">
              {neighbours.map((n) => (
                <li key={n.id} className="flex min-h-11 items-center border-b border-line text-sm">
                  {n.kind === "ghost" ? (
                    <span className="px-1 text-muted">
                      {n.title}
                      <span className="text-dim">, no note yet</span>
                    </span>
                  ) : (
                    <button type="button" onClick={clicked(n)} className="min-h-11 w-full truncate px-1 text-left text-text focus-visible:outline-2 focus-visible:outline-accent">
                      {n.title}
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {(unmeasured || otherText) && (
        <p className="flex flex-none gap-3 border-t border-line px-3 py-1.5 font-mono text-[11px] text-dim">
          {unmeasured && <span>Similarity not measured yet</span>}
          {otherText && <span>{otherText}</span>}
        </p>
      )}
    </aside>
  );
}
