// SPDX-License-Identifier: AGPL-3.0-or-later
import { CalendarDays, Eye, Ghost, List, Maximize, Paperclip, SlidersHorizontal, Sparkles, type LucideIcon } from "lucide-react";
import { useCallback, useId, useMemo, useRef, useState, type FocusEvent, type KeyboardEvent, type MouseEvent, type PointerEvent } from "react";
import { useNavigate } from "react-router-dom";
import { HttpError } from "../api/client";
import { isVaultBusy } from "../api/error-text";
import { useTree } from "../api/notes";
import { vaultOf, viewUrl } from "../api/paths";
import { useErrorTextFor } from "../discreet/errorText";
import { useOffline } from "../offline/mode";
import { mediaQueryHook } from "../hooks/mediaQuery";
import { useWide } from "../hooks/useWide";
import { useOpenNote } from "../shell/openInPane";
import { useGraphPaths, useGraphSimilarity, useLocalGraph, type GraphFilters, type GraphPathsView } from "./api";
import GraphCanvas from "./GraphCanvas";
import { toModel, type GNode, type SizeBy } from "./model";
import NodeCard from "./NodeCard";
import NodePreview from "./NodePreview";
import { edgeSimilarities, pairKey, similarityLine } from "./similarity";
import SuggestionCard from "./SuggestionCard";
import { useSuggestedLinks, type Suggestion } from "./suggestions";
import { useGraphPrefs, type GraphPrefs } from "./useGraphPrefs";

/** Tailwind's `sm` (40rem): from here on the dialog's toolbar fits one row and Open beside is offered. */
const useSmUp = mediaQueryHook("(min-width: 40rem)");

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

type Anchored = { node: GNode; anchor: { x: number; y: number }; width: number; height: number; on: string };

/**
 * The local graph of `path` (the focused pane's note) with its toolbar, the choices remembered per browser, and a foot
 * line on the similarity and on `otherPath`, the other pane's note; the simulation runs only while `running`. In the
 * column a click opens a node. In the dialog a click selects it and shows its card, whose Open opens it and then calls
 * `onOpened`, as the list does; Escape clears the selection before it can close the dialog.
 */
export default function GraphView({ path, otherPath, running, mode, onOpened }: { path: string; otherPath: string | null; running: boolean; mode: "column" | "dialog"; onOpened?(): void }) {
  const id = useId();
  const [prefs, change] = useGraphPrefs();
  // The filters alone, so the size or a layer switched never asks the server again.
  const { depth, days, attachments, ghosts } = prefs;
  const filters = useMemo<GraphFilters>(() => ({ depth, days, attachments, ghosts }), [depth, days, attachments, ghosts]);
  // The graph is the server's: offline it asks nothing and says so, also where it was drawn before (graph design §3).
  const offline = useOffline();
  const asking = running && !offline;
  const local = useLocalGraph(path, filters, asking);
  const similar = useGraphSimilarity(path, filters, asking);
  const tree = useTree(vaultOf(path));
  const errorFor = useErrorTextFor(path);
  const other = useGraphPaths(path, otherPath, filters, asking);
  const [fitSignal, setFitSignal] = useState(0);
  const openNote = useOpenNote();
  const navigate = useNavigate();
  const smUp = useSmUp();
  const wide = useWide();
  const dialog = mode === "dialog";
  // Below sm the dialog's row has room for depth, List and Fit only; the rest is behind Options.
  const folded = dialog && !smUp;
  const [options, setOptions] = useState(false);

  // The last graph stands in while the next is asked for only if the note is in it; else it would show another note's.
  const stale = local.isPlaceholderData && !local.data?.nodes.some((n) => n.path === path);
  // Memoised per answer, or the layout would warm again on every render.
  const plain = useMemo(() => (local.data && !stale && !offline ? toModel(local.data, tree.data) : null), [local.data, stale, offline, tree.data]);
  // Each edge with its similarity, and the foot's line on it: how many distinct links between two notes are measured.
  const { model, similarityText } = useMemo(() => {
    if (!plain) return { model: null, similarityText: null };
    const byId = new Map(plain.nodes.map((n) => [n.id, n]));
    const ends = plain.edges.map((e) => [byId.get(e.source)!.key, byId.get(e.target)!.key] as const);
    const scores = edgeSimilarities(ends, similar.data);
    const isNote = (id: string) => ["note", "day"].includes(byId.get(id)!.kind);
    const links = new Map<string, boolean>();
    plain.edges.forEach((e, i) => {
      if (e.source !== e.target && isNote(e.source) && isNote(e.target)) links.set(pairKey(...ends[i]), scores[i].measured);
    });
    // Another graph's answer (the filters just changed) may score these edges where it shares them, but says nothing of the rest.
    const answer = similar.isPlaceholderData ? undefined : similar.data;
    const measured = [...links.values()].filter(Boolean).length;
    return {
      model: { nodes: plain.nodes, edges: plain.edges.map((e, i) => ({ ...e, s: scores[i].score })) },
      similarityText: similarityLine(links.size, measured, answer, isVaultBusy(similar.error)),
    };
  }, [plain, similar.data, similar.isPlaceholderData, similar.error]);
  const neighbours = useMemo(
    () => (model?.nodes ?? []).filter((n) => n.depth > 0).sort((a, b) => a.depth - b.depth || a.title.localeCompare(b.title)),
    [model],
  );

  // An attachment opens in its window either way: panes hold notes only.
  const open = (n: GNode, beside: boolean) => {
    if (!n.path) return;
    if (n.kind === "attachment") navigate(viewUrl(n.path), { state: { paths: [n.path] } });
    else openNote(n.path, beside ? { side: "right" } : undefined);
    onOpened?.();
  };
  const clicked = (n: GNode) => (e: MouseEvent) => open(n, e.ctrlKey || e.metaKey);

  const otherText = offline ? null : otherLine(other.data);

  const inGraph = useMemo(() => new Set(model?.nodes.flatMap((n) => (n.path ? [n.path] : [])) ?? []), [model]);
  const suggestions = useSuggestedLinks(path, inGraph, prefs.suggestions && !!model);

  // Where a card goes: beside the pointer, or the node that has the keyboard's focus, in the body's coordinates.
  const body = useRef<HTMLDivElement>(null);
  const spot = useRef({ x: 0, y: 0 });
  const bodySize = useRef({ width: 0, height: 0 });
  const at = (clientX: number, clientY: number) => {
    const box = body.current!.getBoundingClientRect();
    spot.current = { x: clientX - box.left, y: Math.min(clientY - box.top, box.height - 160) };
    bodySize.current = { width: box.width, height: box.height };
  };
  const pointed = (e: PointerEvent) => at(e.clientX, e.clientY);
  const focused = (e: FocusEvent) => {
    const r = e.target.getBoundingClientRect();
    at(r.right, r.top + r.height / 2);
  };
  // Each kept with the note it was opened on, so neither outlives a new centre.
  const anchored = (node: GNode): Anchored => ({ node, anchor: spot.current, ...bodySize.current, on: path });
  const [hover, setHover] = useState<Anchored | null>(null);
  // With the preview off a hover only clears, so a card left over can't come back when it is switched on again.
  const hovered = (n: GNode | null) => setHover(n && prefs.preview ? anchored(n) : null);
  const [selection, setSelection] = useState<Anchored | null>(null);
  // Gone with a new centre, or once its node leaves the graph (a filter switched, a smaller depth).
  const selected = dialog && selection?.on === path && model?.nodes.some((n) => n.key === selection.node.key) ? selection : null;
  const card = useRef<HTMLDivElement>(null);
  const [suggestion, setSuggestion] = useState<{ s: Suggestion; on: string } | null>(null);
  const closeSuggestion = useCallback(() => setSuggestion(null), []);

  // Held here, not on the card, so it answers wherever the focus is: on the node, on the card or in the toolbar.
  const keyDown = (e: KeyboardEvent) => {
    if (e.key !== "Escape" || !selected) return;
    e.stopPropagation();
    e.preventDefault();
    // The card's buttons go with it, so the focus goes back to the selected node rather than to the page.
    if (card.current?.contains(e.target as Node)) body.current?.querySelector<HTMLElement>('[aria-pressed="true"]')?.focus();
    setSelection(null);
  };

  const switches = (
    <>
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
    </>
  );

  return (
    <div onKeyDown={dialog ? keyDown : undefined} className="flex min-h-0 flex-1 flex-col">
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
        {!folded && switches}
        <span className="flex-1" />
        {folded && (
          <button type="button" aria-label="Options" title="Options" aria-expanded={options} aria-controls={`${id}-options`} onClick={() => setOptions((o) => !o)} className={tool}>
            <SlidersHorizontal size={16} strokeWidth={1.8} aria-hidden />
          </button>
        )}
        <button type="button" aria-label="List" title="List" aria-expanded={prefs.list} aria-controls={`${id}-list`} onClick={() => change({ list: !prefs.list })} className={tool}>
          <List size={16} strokeWidth={1.8} aria-hidden />
        </button>
        <button type="button" aria-label="Fit" title="Fit" onClick={() => setFitSignal((n) => n + 1)} className={tool}>
          <Maximize size={16} strokeWidth={1.8} aria-hidden />
        </button>
      </div>
      {folded && options && (
        <div id={`${id}-options`} className="flex flex-none items-center gap-1 border-b border-line px-3 py-2">
          {switches}
        </div>
      )}
      {/* On the way down: a node keeps its press from the canvas's background, and would keep it from here too. */}
      <div ref={body} onPointerDownCapture={pointed} onPointerMove={pointed} onFocus={focused} className="relative flex min-h-0 flex-1 flex-col">
        {offline ? (
          <p className="m-auto px-6 text-center text-sm text-muted">The graph needs a connection. It's back when you are.</p>
        ) : local.isError && !model ? (
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
            running={running}
            select={dialog}
            selectedKey={selected?.node.key ?? null}
            // The selected node tapped again opens it, beside where the split view fits (from 1280 px, as the tree's Open beside).
            onSelect={(n) => (n && n.key === selected?.node.key ? open(n, wide) : setSelection(n && anchored(n)))}
          />
        ) : (
          <p className="m-auto text-sm text-muted">Drawing the graph…</p>
        )}
        {/* The selected node's card says all the hover card would. */}
        {model && prefs.preview && hover?.on === path && hover.node.key !== selected?.node.key && <NodePreview node={hover.node} anchor={hover.anchor} width={hover.width} />}
        {model && selected && <NodeCard ref={card} node={selected.node} anchor={selected.anchor} width={selected.width} height={selected.height} docked={!smUp} beside={smUp} onOpen={open} />}
        {model && suggestion?.on === path && <SuggestionCard from={path} suggestion={suggestion.s} onClose={closeSuggestion} />}
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
      {(similarityText || otherText) && (
        <p className="flex flex-none flex-wrap gap-x-3 border-t border-line px-3 py-1.5 font-mono text-[11px] text-dim">
          {similarityText && <span>{similarityText}</span>}
          {otherText && <span>{otherText}</span>}
        </p>
      )}
    </div>
  );
}
