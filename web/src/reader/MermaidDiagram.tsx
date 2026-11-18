// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useId, useMemo, useRef, useState } from "react";
import { drawing, lines } from "./diagramSize";

type Render = typeof import("./mermaidRender");

let loading: Promise<Render> | null = null;
// A failed load is forgotten, so the next diagram tries again.
const load = () =>
  (loading ??= import("./mermaidRender").catch((e: unknown) => {
    loading = null;
    throw e;
  }));

type State = { kind: "drawing" } | { kind: "drawn"; url: string; alt: string } | { kind: "failed"; message: string } | { kind: "unavailable" };

const buttonClass = "min-h-11 rounded-[8px] px-3 text-[14px] text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent";
// Draw diagram stands alone in its box: a 48 px target.
const drawClass = "min-h-12 rounded-[8px] px-3 text-[14px] text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent";

/**
 * A Mermaid diagram as an image of the SVG Mermaid draws: its SVG never becomes markup of the page, so neither its
 * `<style>` nor anything else in it touches the page. Its URL is a `data:` URL, never a `blob:` one, and it is never
 * inside a link: a blob SVG opened on its own would be a document on the page's origin, while a page can't open a
 * `data:` URL as a document, and one opened by hand from the address bar has an origin of its own. A large diagram waits for Draw diagram, as does one the note leaves no place among
 * those drawing by themselves (`auto`); the largest are not drawn here.
 */
export default function MermaidDiagram({ source, dark, auto = true }: { source: string; dark: boolean; auto?: boolean }) {
  const how = useMemo(() => drawing(source), [source]);
  const [asked, setAsked] = useState(false);
  const draws = how !== "never" && (asked || (auto && how === "itself"));
  const [state, setState] = useState<State>({ kind: "drawing" });
  const [open, setOpen] = useState(false);
  const [wide, setWide] = useState(false);
  const [full, setFull] = useState(false);
  const frame = useRef<HTMLDivElement>(null);
  const image = useRef<HTMLImageElement>(null);
  const codeId = `mermaid-${useId().replace(/[^a-zA-Z0-9-]/g, "")}`;

  useEffect(() => {
    if (!draws) return;
    let cancelled = false;
    const stop = new AbortController();
    load().then(
      (m) =>
        m.renderDiagram(source, dark, stop.signal).then(
          ({ svg, alt }) => {
            if (cancelled) return;
            setFull(false);
            setState({ kind: "drawn", url: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`, alt });
          },
          (e: unknown) => !cancelled && setState({ kind: "failed", message: e instanceof Error ? e.message : String(e) }),
        ),
      () => !cancelled && setState({ kind: "unavailable" }),
    );
    return () => {
      cancelled = true;
      stop.abort();
    };
  }, [source, dark, draws]);

  const url = state.kind === "drawn" ? state.url : undefined;

  const measure = () => setWide(!!image.current && !!frame.current && image.current.naturalWidth > frame.current.clientWidth);
  useEffect(() => {
    if (!url) return;
    addEventListener("resize", measure);
    return () => removeEventListener("resize", measure);
  }, [url]);

  const code = (
    <pre
      id={codeId}
      hidden={state.kind === "drawn" && !open}
      className="my-3 overflow-x-auto rounded-[10px] border border-line bg-surface px-3.5 py-3 font-mono text-[12.5px] leading-[1.6] [&>code]:bg-transparent [&>code]:p-0"
    >
      <code className="language-mermaid">{source}</code>
    </pre>
  );

  if (how === "never") {
    return (
      <div className="my-3">
        <p role="note" className="text-[14px] text-muted">
          This diagram is too large to draw here.
        </p>
        {code}
      </div>
    );
  }
  if (!draws) {
    return (
      <div className="my-3 rounded-[10px] border border-line bg-surface px-3.5 py-3">
        <pre className="overflow-x-auto font-mono text-[12.5px] leading-[1.6] text-muted">{lines(source).slice(0, 3).join("\n")}</pre>
        <button type="button" onClick={() => setAsked(true)} className={drawClass}>
          Draw diagram
        </button>
      </div>
    );
  }
  if (state.kind === "drawing") return <p className="my-3 text-[14px] text-muted">Drawing the diagram …</p>;
  if (state.kind === "failed" || state.kind === "unavailable") {
    return (
      <div className="my-3">
        <p role="note" className="text-[14px] text-muted">
          {state.kind === "failed" ? `This diagram can't be drawn: ${state.message}` : "Diagrams can't be drawn right now."}
        </p>
        {code}
      </div>
    );
  }
  return (
    <figure className="my-3">
      <div ref={frame} className="overflow-x-auto">
        <img
          ref={image}
          src={state.url}
          alt={state.alt}
          onLoad={measure}
          onError={() => setState({ kind: "failed", message: "the image could not be shown." })}
          className={full ? "h-auto max-w-none" : "h-auto max-w-full"}
        />
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="button" aria-expanded={open} aria-controls={codeId} onClick={() => setOpen((o) => !o)} className={buttonClass}>
          {open ? "Hide source" : "Show source"}
        </button>
        {(wide || full) && (
          <button type="button" aria-pressed={full} onClick={() => setFull((f) => !f)} className={buttonClass}>
            {full ? "Fit" : "Full size"}
          </button>
        )}
      </div>
      {code}
    </figure>
  );
}
