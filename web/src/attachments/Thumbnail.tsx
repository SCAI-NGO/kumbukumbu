// SPDX-License-Identifier: AGPL-3.0-or-later
import { FileText, Image as ImageIcon } from "lucide-react";
import { createContext, useContext, useEffect, useRef, useState, type RefObject } from "react";
import { fetchFile, useAttachmentUrl } from "../api/notes";
import { fileKindOf, inVault, type FileAt } from "../api/paths";
import { openPdf } from "./pdf";

/**
 * The scrolling list the thumbnails are near to or far from, when they scroll inside one (the gallery): null while
 * it isn't there yet. Without one (no provider), the screen is what they are near to.
 */
export const ScrollRoot = createContext<HTMLElement | null | undefined>(undefined);

/**
 * Within about a screen's height of the visible part (above or below): only then does a thumbnail hold its picture.
 * A screen ahead loads before it is reached; further away the picture goes again, which bounds what a phone holds
 * (iOS Safari above all) at the price of fetching it anew on the way back.
 */
export function useNear(element: RefObject<HTMLElement>) {
  const root = useContext(ScrollRoot);
  const [near, setNear] = useState(typeof IntersectionObserver === "undefined");
  useEffect(() => {
    const target = element.current;
    if (!target || root === null || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(([entry]) => setNear(entry.isIntersecting), { root, rootMargin: "100% 0px" });
    observer.observe(target);
    return () => observer.disconnect();
  }, [element, root]);
  return near;
}

/** How a thumbnail fills its box: cropped to it (a tile), or whole inside it (a comparison). */
export type Fit = "cover" | "contain";

type Props = { vault: string; path: string; at?: FileAt; fit?: Fit; alt?: string };

/**
 * An attachment's picture in a box of the caller's (positioned, sized): an image scaled by the browser, a PDF's
 * first page. It holds its picture only while near the screen (`useNear`): far from it, an image's object URL is
 * revoked and a PDF's canvas emptied.
 */
export default function Thumbnail(props: Props) {
  const box = useRef<HTMLSpanElement>(null);
  const near = useNear(box);
  return (
    <span ref={box} className="absolute inset-0">
      {near && <FileThumb {...props} />}
    </span>
  );
}

/** The picture itself, for a caller that tells nearness itself (the gallery's tiles). `path` from the root of the tree. */
export function FileThumb({ vault, path, at = {}, fit = "cover", alt = "" }: Props) {
  return fileKindOf(path) === "pdf" ? <PdfThumb vault={vault} path={path} at={at} fit={fit} alt={alt} /> : <ImageThumb vault={vault} path={path} at={at} fit={fit} alt={alt} />;
}

function Placeholder({ pdf, alt }: { pdf: boolean; alt: string }) {
  const Icon = pdf ? FileText : ImageIcon;
  return <Icon size={28} role={alt ? "img" : undefined} aria-label={alt || undefined} aria-hidden={alt ? undefined : true} className="absolute inset-0 m-auto text-dim" />;
}

const fitClass = { cover: "object-cover", contain: "object-contain" };

/** The image itself, scaled by the browser (there are no thumbnails on the server in this part); fetched in turn. */
function ImageThumb({ vault, path, at, fit, alt }: Required<Props>) {
  const url = useAttachmentUrl(vault, inVault(path), at.version, at.checkpoint, imageThumbTurn);
  const [broken, setBroken] = useState(false);
  if (!url || broken) return <Placeholder pdf={false} alt={alt} />;
  return <img src={url} alt={alt} draggable={false} decoding="async" onError={() => setBroken(true)} className={`absolute inset-0 size-full ${fitClass[fit]}`} />;
}

/** A canvas never larger than this many pixels, whatever the screen's density. */
const maxThumbPixels = 1_000_000;

/**
 * A turn, once one of `max` is free; the function it answers ends the turn (again does nothing). Aborted while
 * waiting, it leaves the queue and rejects.
 */
export type Turn = (signal: AbortSignal) => Promise<() => void>;

/** A queue of turns, at most `max` of them taken at once. */
export function turns(max: number): Turn {
  let taken = 0;
  const waiting: (() => void)[] = [];
  return (signal) => new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    const take = () => {
      taken++;
      let ended = false;
      resolve(() => {
        if (ended) return;
        ended = true;
        taken--;
        waiting.shift()?.();
      });
    };
    if (taken < max) return take();
    const turn = () => {
      signal.removeEventListener("abort", abort);
      take();
    };
    const abort = () => {
      waiting.splice(waiting.indexOf(turn), 1);
      reject(signal.reason);
    };
    waiting.push(turn);
    signal.addEventListener("abort", abort, { once: true });
  });
}

/**
 * At most this many PDF thumbnails fetch and draw at once, the others wait their turn: a history sheet shows all of a
 * PDF's versions at once, each up to 15 MB and a pdf.js worker of its own while it is drawn.
 */
export const maxPdfThumbs = 2;
export const pdfThumbTurn = turns(maxPdfThumbs);

/**
 * At most this many image thumbnails fetch at once: a folder of phone photos near the screen would otherwise ask the
 * server for all of their bytes together (behind HTTP/2 no connection limit holds them back), and the server holds
 * each in memory while it serves it.
 */
export const maxImageThumbs = 6;
export const imageThumbTurn = turns(maxImageThumbs);

/**
 * A PDF's first page at the box's size, drawn by pdf.js; the document is let go of as soon as it is drawn, and the
 * canvas emptied when the thumbnail goes (browsers cap what all canvases together may hold, iOS Safari above all).
 */
function PdfThumb({ vault, path, at, fit, alt }: Required<Props>) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [drawn, setDrawn] = useState(false);
  const { version, checkpoint } = at;

  useEffect(() => {
    let task: Awaited<ReturnType<typeof openPdf>> | undefined;
    let render: { cancel(): void } | undefined;
    let endTurn: (() => void) | undefined;
    let ended = false;
    const controller = new AbortController();
    const target = canvas.current;
    void (async () => {
      try {
        endTurn = await pdfThumbTurn(controller.signal);
        if (ended) return;
        const { data, response } = await fetchFile(vault, inVault(path), { version, checkpoint }, "arrayBuffer", controller.signal);
        if (ended || !response.ok || !data || !target) return;
        task = await openPdf(data as ArrayBuffer);
        if (ended) return;
        const page = await (await task.promise).getPage(1);
        if (ended) return;
        const base = page.getViewport({ scale: 1 });
        const wanted = ((target.clientWidth || 160) / base.width) * (window.devicePixelRatio || 1);
        const viewport = page.getViewport({ scale: Math.min(wanted, Math.sqrt(maxThumbPixels / (base.width * base.height))) });
        target.width = Math.floor(viewport.width);
        target.height = Math.floor(viewport.height);
        const drawing = page.render({ canvas: target, viewport });
        render = drawing;
        await drawing.promise;
        if (!ended) setDrawn(true);
      } catch {
        // Not readable, not a PDF pdf.js opens, or let go of for another: the thumbnail keeps its icon.
      } finally {
        // Drawn (or not): the document and its worker go; the canvas keeps the pixels.
        void task?.destroy();
        task = undefined;
        endTurn?.();
      }
    })();
    return () => {
      ended = true;
      controller.abort();
      render?.cancel();
      void task?.destroy();
      // Gone: the next thumbnail need not wait for this one's fetch or drawing to wind down.
      endTurn?.();
      if (target) {
        target.width = 0;
        target.height = 0;
      }
    };
  }, [vault, path, version, checkpoint]);

  return (
    <>
      {!drawn && <Placeholder pdf alt={alt} />}
      <canvas
        ref={canvas}
        role={alt ? "img" : undefined}
        aria-label={alt || undefined}
        aria-hidden={alt ? undefined : true}
        className={`absolute inset-0 size-full ${fit === "cover" ? "bg-white object-cover object-top" : "object-contain"}`}
        style={{ visibility: drawn ? "visible" : "hidden" }}
      />
    </>
  );
}
