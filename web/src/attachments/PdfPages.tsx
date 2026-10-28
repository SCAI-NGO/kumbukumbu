// SPDX-License-Identifier: AGPL-3.0-or-later
import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { PDFDocumentProxy } from "pdfjs-dist/legacy/build/pdf.mjs";
import { fetchFile } from "../api/notes";
import type { FileAt } from "../api/paths";
import { wheelFactor } from "./gestures";
import { openPdf, pageLinks, type PageLink } from "./pdf";

/** What the lightbox shows of a PDF: the page in view, how many there are, the zoom. */
export interface PdfStatus {
  page: number;
  pages: number;
  zoom: number;
}

const maxZoom = 4;
/** iOS Safari draws no canvas over 16.7 million pixels; a page stays below. */
const maxCanvasPixels = 16_000_000;

type Failure = "unreadable" | "damaged";

/**
 * A PDF's pages one below the other (attachments design, "Lightbox"), drawn by pdf.js into canvases as they come
 * into view. Scrolling is the browser's; two fingers (or Ctrl and the wheel, a trackpad's pinch) zoom, after which
 * the pages are drawn again sharp. A swipe sideways without zoom asks for the next or previous file.
 */
export default function PdfPages({
  vault,
  path,
  at = {},
  onStatus,
  onSwipe,
}: {
  vault: string;
  path: string;
  /** An earlier version, or a checkpoint's state, instead of today's. */
  at?: FileAt;
  onStatus(status: PdfStatus): void;
  onSwipe(swipe: "next" | "previous"): void;
}) {
  const { version, checkpoint } = at;
  const [pdf, setPdf] = useState<PDFDocumentProxy>();
  const [failure, setFailure] = useState<Failure>();
  const [ratio, setRatio] = useState(1.414);
  const [zoom, setZoom] = useState(1);
  const [page, setPage] = useState(1);
  const scroller = useRef<HTMLDivElement>(null);
  const anchor = useRef<{ x: number; y: number; from: number } | null>(null);
  const zoomRef = useRef(zoom);
  zoomRef.current = zoom;
  const status = useRef(onStatus);
  status.current = onStatus;
  const swipe = useRef(onSwipe);
  swipe.current = onSwipe;

  useEffect(() => {
    let task: Awaited<ReturnType<typeof openPdf>> | undefined;
    let ended = false;
    const controller = new AbortController();
    void (async () => {
      const { data, response } = await fetchFile(vault, path, { version, checkpoint }, "arrayBuffer", controller.signal).catch(() => ({
        data: undefined,
        response: undefined,
      }));
      if (ended) return;
      if (!response?.ok || !data) return setFailure("unreadable");
      try {
        task = await openPdf(data as ArrayBuffer);
      } catch {
        // pdf.js itself didn't load (its chunk comes over the network): the PDF is not to blame.
        if (!ended) setFailure("unreadable");
        return;
      }
      try {
        if (ended) return void task.destroy();
        const doc = await task.promise;
        const first = await doc.getPage(1);
        const { width, height } = first.getViewport({ scale: 1 });
        if (ended) return;
        setRatio(height / width);
        setPdf(doc);
      } catch {
        if (!ended) setFailure("damaged");
      }
    })();
    return () => {
      ended = true;
      controller.abort();
      void task?.destroy();
    };
  }, [vault, path, version, checkpoint]);

  useEffect(() => {
    if (pdf) status.current({ page, pages: pdf.numPages, zoom });
  }, [pdf, page, zoom]);

  // A zoom keeps the spot between the fingers (or under the pointer) where it was.
  useLayoutEffect(() => {
    const element = scroller.current;
    const at = anchor.current;
    if (!element || !at) return;
    anchor.current = null;
    const factor = zoom / at.from;
    element.scrollLeft = (element.scrollLeft + at.x) * factor - at.x;
    element.scrollTop = (element.scrollTop + at.y) * factor - at.y;
  }, [zoom]);

  // Pinch and swipe with touch events: the browser keeps scrolling with one finger, two are the app's.
  useEffect(() => {
    const element = scroller.current;
    if (!element) return;
    let pinch: { distance: number; zoom: number } | null = null;
    let start: { x: number; y: number } | null = null;
    const spread = (t: TouchList) => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY) || 1;
    const zoomTo = (next: number, x: number, y: number) => {
      const clamped = Math.min(maxZoom, Math.max(1, next));
      if (clamped === zoomRef.current) return;
      const rect = element.getBoundingClientRect();
      anchor.current = { x: x - rect.left, y: y - rect.top, from: zoomRef.current };
      zoomRef.current = clamped;
      setZoom(clamped);
    };
    const onTouchStart = (e: TouchEvent) => {
      if (e.touches.length === 2) {
        pinch = { distance: spread(e.touches), zoom: zoomRef.current };
        start = null;
      } else if (e.touches.length === 1) {
        start = { x: e.touches[0].clientX, y: e.touches[0].clientY };
      }
    };
    const onTouchMove = (e: TouchEvent) => {
      if (!pinch || e.touches.length < 2) return;
      e.preventDefault();
      const [a, b] = [e.touches[0], e.touches[1]];
      zoomTo((pinch.zoom * spread(e.touches)) / pinch.distance, (a.clientX + b.clientX) / 2, (a.clientY + b.clientY) / 2);
    };
    const onTouchEnd = (e: TouchEvent) => {
      if (e.touches.length < 2) pinch = null;
      if (!start || e.touches.length > 0) return;
      const touch = e.changedTouches[0];
      const dx = touch.clientX - start.x;
      const dy = touch.clientY - start.y;
      start = null;
      if (zoomRef.current === 1 && Math.abs(dx) > 60 && Math.abs(dx) > 2 * Math.abs(dy)) swipe.current(dx < 0 ? "next" : "previous");
    };
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      zoomTo(zoomRef.current * wheelFactor(e), e.clientX, e.clientY);
    };
    element.addEventListener("touchstart", onTouchStart, { passive: true });
    element.addEventListener("touchmove", onTouchMove, { passive: false });
    element.addEventListener("touchend", onTouchEnd, { passive: true });
    element.addEventListener("touchcancel", onTouchEnd, { passive: true });
    element.addEventListener("wheel", onWheel, { passive: false });
    return () => {
      element.removeEventListener("touchstart", onTouchStart);
      element.removeEventListener("touchmove", onTouchMove);
      element.removeEventListener("touchend", onTouchEnd);
      element.removeEventListener("touchcancel", onTouchEnd);
      element.removeEventListener("wheel", onWheel);
    };
  }, []);

  // The page in view: the last whose top is above the middle of the scroller.
  const onScroll = () => {
    const element = scroller.current;
    if (!element) return;
    const middle = element.scrollTop + element.clientHeight / 2;
    const pages = [...element.querySelectorAll<HTMLElement>("[data-page]")];
    const current = pages.filter((p) => p.offsetTop <= middle).at(-1);
    setPage(current ? Number(current.dataset.page) : 1);
  };

  const goTo = async (dest: unknown) => {
    if (!pdf) return;
    try {
      const explicit = typeof dest === "string" ? await pdf.getDestination(dest) : dest;
      if (!Array.isArray(explicit)) return;
      const target = explicit[0];
      const index = typeof target === "number" ? target : await pdf.getPageIndex(target);
      scroller.current?.querySelector(`[data-page="${index + 1}"]`)?.scrollIntoView({ block: "start" });
    } catch {
      // A link to nowhere in the PDF does nothing.
    }
  };

  if (failure) {
    return (
      <p role="alert" className="absolute inset-x-6 top-1/2 -translate-y-1/2 text-center text-[#d9d6cf]">
        {failure === "unreadable" ? "This PDF can't be loaded. Check the connection and try again." : "This PDF can't be shown: it seems to be damaged."}
      </p>
    );
  }

  return (
    <div ref={scroller} onScroll={onScroll} data-testid="pdf-pages" className="absolute inset-0 overflow-auto overscroll-contain bg-[#1a1a1e]">
      {!pdf && <p className="m-auto pt-32 text-center text-[#a09d96]">Loading the PDF …</p>}
      {pdf && (
        <div className="mx-auto flex flex-col items-center gap-3 px-3 pt-16 pb-24" style={{ width: `${zoom * 100}%`, maxWidth: `${zoom * 60}rem` }}>
          {Array.from({ length: pdf.numPages }, (_, i) => (
            <PdfPage key={i} pdf={pdf} number={i + 1} ratio={ratio} zoom={zoom} onDestination={goTo} />
          ))}
        </div>
      )}
    </div>
  );
}

function release(target: HTMLCanvasElement | null) {
  if (!target) return;
  target.width = 0;
  target.height = 0;
}

/** One page: a placeholder of the first page's shape until it comes into view, then the canvas and its links. */
const PdfPage = memo(function PdfPage({
  pdf,
  number,
  ratio,
  zoom,
  onDestination,
}: {
  pdf: PDFDocumentProxy;
  number: number;
  ratio: number;
  zoom: number;
  onDestination(dest: unknown): void;
}) {
  const box = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  // Near the screen (within a screen's height of it): only then does the page hold a drawn canvas.
  const [near, setNear] = useState(typeof IntersectionObserver === "undefined");
  const [shape, setShape] = useState(ratio);
  const [links, setLinks] = useState<PageLink[]>([]);
  const linksRead = useRef(false);

  useEffect(() => {
    const element = box.current;
    if (!element || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(([entry]) => setNear(entry.isIntersecting), { rootMargin: "100% 0px" });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  // A canvas holds its pixels until it is emptied: browsers (iOS Safari above all) cap what all canvases together may
  // hold, and a long PDF would go blank. So a page far from the screen, or of a PDF no longer shown, keeps none.
  useEffect(() => {
    const target = canvas.current;
    return () => release(target);
  }, []);

  // Drawn when it comes near, and again for a new zoom once the fingers have rested a moment; emptied when it leaves.
  useEffect(() => {
    if (!near) return release(canvas.current);
    let task: { cancel(): void } | undefined;
    let ended = false;
    const timer = setTimeout(async () => {
      try {
        const page = await pdf.getPage(number);
        if (ended) return;
        const base = page.getViewport({ scale: 1 });
        setShape(base.height / base.width);
        const cssWidth = box.current?.clientWidth || base.width;
        const wanted = (cssWidth / base.width) * (window.devicePixelRatio || 1);
        const scale = Math.min(wanted, Math.sqrt(maxCanvasPixels / (base.width * base.height)));
        const viewport = page.getViewport({ scale });
        // In fractions of the page: read once, whatever the zoom.
        if (!linksRead.current) {
          linksRead.current = true;
          setLinks(await pageLinks(page, viewport));
        }
        const target = canvas.current;
        if (!target || ended) return;
        target.width = Math.floor(viewport.width);
        target.height = Math.floor(viewport.height);
        const render = page.render({ canvas: target, viewport });
        task = render;
        await render.promise;
      } catch {
        // Cancelled for a newer drawing, or a page that doesn't draw: it stays blank.
      }
    }, zoom === 1 ? 0 : 200);
    return () => {
      ended = true;
      clearTimeout(timer);
      task?.cancel();
    };
  }, [pdf, number, near, zoom]);

  return (
    <div ref={box} data-page={number} className="relative w-full bg-white shadow-md" style={{ aspectRatio: `1 / ${shape}` }}>
      <canvas ref={canvas} aria-label={`Page ${number}`} className="absolute inset-0 size-full" />
      {links.map((link, i) => {
        const style = { left: `${link.left * 100}%`, top: `${link.top * 100}%`, width: `${link.width * 100}%`, height: `${link.height * 100}%` };
        return link.url ? (
          <a key={i} href={link.url} target="_blank" rel="noopener noreferrer" title={link.url} className="absolute" style={style}>
            <span className="sr-only">{link.url}</span>
          </a>
        ) : (
          <button key={i} type="button" aria-label="Go to the place in this PDF" onClick={() => onDestination(link.dest)} className="absolute" style={style} />
        );
      })}
    </div>
  );
});
