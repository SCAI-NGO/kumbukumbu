// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useId, useRef, useState } from "react";

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

/**
 * A Mermaid diagram as an image of the SVG Mermaid draws: its SVG never becomes markup of the page, so neither its
 * `<style>` nor anything else in it touches the page. Never inside a link: a blob SVG opened on its own would be a
 * document on the page's origin.
 */
export default function MermaidDiagram({ source, dark }: { source: string; dark: boolean }) {
  const [state, setState] = useState<State>({ kind: "drawing" });
  const [open, setOpen] = useState(false);
  const [wide, setWide] = useState(false);
  const [full, setFull] = useState(false);
  const frame = useRef<HTMLDivElement>(null);
  const image = useRef<HTMLImageElement>(null);
  const codeId = `mermaid-${useId().replace(/[^a-zA-Z0-9-]/g, "")}`;

  useEffect(() => {
    let cancelled = false;
    load().then(
      (m) =>
        m.renderDiagram(source, dark).then(
          ({ svg, alt }) => {
            if (cancelled) return;
            setState({ kind: "drawn", url: URL.createObjectURL(new Blob([svg], { type: "image/svg+xml" })), alt });
          },
          (e: unknown) => !cancelled && setState({ kind: "failed", message: e instanceof Error ? e.message : String(e) }),
        ),
      () => !cancelled && setState({ kind: "unavailable" }),
    );
    return () => {
      cancelled = true;
    };
  }, [source, dark]);

  // The URL goes when the image goes, or when the next one has taken its place.
  const url = state.kind === "drawn" ? state.url : undefined;
  useEffect(() => (url ? () => URL.revokeObjectURL(url) : undefined), [url]);

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
        <img ref={image} src={state.url} alt={state.alt} onLoad={measure} className={full ? "h-auto max-w-none" : "h-auto max-w-full"} />
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
