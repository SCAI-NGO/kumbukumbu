// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronLeft, ChevronRight, Download, Ellipsis, FolderInput, Info, Link2, Trash2, X, type LucideIcon } from "lucide-react";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { findFolder, useAttachmentUrl, useFileInfo, useTree, type TreeView } from "../api/notes";
import { fileKindOf, fileUrl, folderOf, inVault, isAttachment, titleOf, viewUrl, type LightboxState } from "../api/paths";
import { Menu } from "../components/Menu";
import { sizeText, timeText } from "../components/Sheet";
import { topDialog } from "../hooks/useDialog";
import MoveDialog from "../shell/MoveDialog";
import DeleteFileDialog from "./DeleteFileDialog";
import { useZoomPan } from "./gestures";
import PdfPages, { type PdfStatus } from "./PdfPages";

// The lightbox is black in every colour scheme, as a photo viewer is: its controls use these, the Kumbu scheme's
// text colours (theme/schemes.ts), which read on black; the dialogs it opens keep the scheme's own.
const controlClass =
  "flex size-11 flex-none items-center justify-center rounded-[10px] bg-[#232328cc] text-[#d9d6cf] disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-[#f0a848]";
const badgeClass = "rounded-[6px] bg-[#232328cc] px-2 py-0.5 font-mono text-[12px] text-[#f0a848]";

/** The router state as the lightbox keeps it: `back` once it knows whether closing goes back to its opener. */
type State = LightboxState & { back?: boolean };

/** Without an order to page through: the attachments of the file's folder, as the tree lists them. */
function folderOrder(tree: TreeView | undefined, path: string): string[] | null {
  const folder = tree && findFolder(tree.root, folderOf(path));
  const paths = folder?.notes.filter((n) => isAttachment(n.path)).map((n) => n.path) ?? [];
  return paths.includes(path) ? paths : null;
}

/**
 * An image or PDF full screen on black (attachments design, "Lightbox"), at `/view/:vault/<path>`. It pages through
 * the paths of its router state (a folder's attachments from the tree, a note's from its chips), else the file's
 * folder: swipe, the arrow keys or the side buttons. Top: close, the name, info, a menu; bottom: download, move,
 * copy a link to paste into a note, delete. Escape, or a swipe down on an image, closes it.
 */
export default function Lightbox() {
  const { vault = "", "*": rest = "" } = useParams();
  const path = `/${vault}/${rest}`;
  const location = useLocation();
  const navigate = useNavigate();
  const state = location.state as State | null;
  const given = state?.paths?.includes(path) ? state.paths : null;
  const tree = useTree(given ? null : vault);
  const paths = given ?? folderOrder(tree.data, path) ?? [path];
  // Opened from within the app, closing goes back there; opened from outside (a bookmark), to the vaults.
  const back = state?.back ?? location.key !== "default";
  const info = useFileInfo(path);
  const kind = fileKindOf(path);
  const name = titleOf(path);

  const [showInfo, setShowInfo] = useState(false);
  const [dialog, setDialog] = useState<"move" | "delete" | null>(null);
  const [copy, setCopy] = useState<"copied" | "failed" | null>(null);
  const [zoom, setZoom] = useState(1);
  const [pdf, setPdf] = useState<PdfStatus | null>(null);
  const download = useRef<HTMLAnchorElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);

  // Focus starts in the lightbox, on the way out of it.
  useEffect(() => closeButton.current?.focus(), []);

  // What is shown of one file starts afresh with the next.
  useEffect(() => {
    setZoom(1);
    setPdf(null);
    setCopy(null);
  }, [path]);

  const at = paths.indexOf(path);
  const previous = at > 0 ? paths[at - 1] : undefined;
  const next = at >= 0 && at < paths.length - 1 ? paths[at + 1] : undefined;

  const show = useCallback(
    (to: string, order = paths) => navigate(viewUrl(to), { replace: true, state: { paths: order, back } satisfies State }),
    [navigate, paths, back],
  );
  const close = useCallback(() => (back ? navigate(-1) : navigate("/", { replace: true })), [back, navigate]);
  const swipe = useCallback(
    (to: "next" | "previous" | "close") => {
      if (to === "close") return close();
      const target = to === "next" ? next : previous;
      if (target) show(target);
    },
    [close, next, previous, show],
  );

  // The keyboard belongs to a dialog while one is open (it answers Escape itself), and to a field being typed in.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (topDialog() || e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement) return;
      if (e.key === "Escape") close();
      else if (e.key === "ArrowLeft" && previous) show(previous);
      else if (e.key === "ArrowRight" && next) show(next);
      else return;
      e.preventDefault();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [close, show, previous, next]);

  // A link as a note would hold it: an image embedded, a PDF linked.
  const copyLink = async () => {
    try {
      await navigator.clipboard.writeText(kind === "pdf" ? `[[${name}]]` : `![[${name}]]`);
      setCopy("copied");
    } catch {
      setCopy("failed");
    }
  };

  const downloadUrl = `${fileUrl(vault, inVault(path))}?download=1`;
  // Deleted only at the version shown, so not before it is known.
  const version = info.data?.version;
  const actions: { label: string; icon: LucideIcon; onSelect(): void; disabled?: boolean }[] = [
    { label: "Download", icon: Download, onSelect: () => download.current?.click() },
    { label: "Rename or move", icon: FolderInput, onSelect: () => setDialog("move") },
    { label: "Copy link", icon: Link2, onSelect: () => void copyLink() },
    { label: "Delete", icon: Trash2, onSelect: () => setDialog("delete"), disabled: !version },
  ];

  const width = info.data?.width;
  const height = info.data?.height;
  const extent = kind === "pdf" ? (pdf ? `${pdf.pages} ${pdf.pages === 1 ? "page" : "pages"}` : null) : width && height ? `${width} × ${height}` : null;

  return (
    <main aria-label={name} className="fixed inset-0 z-50 overflow-hidden bg-black text-[#eceae4]">
      {kind === "pdf" ? (
        <PdfPages key={path} vault={vault} path={inVault(path)} onStatus={setPdf} onSwipe={swipe} />
      ) : (
        <ImageStage key={path} vault={vault} path={path} name={name} onSwipe={swipe} onZoom={setZoom} />
      )}

      <div className="pointer-events-none absolute inset-x-0 top-0 flex flex-col gap-2 bg-gradient-to-b from-black/80 to-transparent px-2 pt-[max(8px,env(safe-area-inset-top))] pb-6">
        <div className="pointer-events-auto flex items-center gap-2">
          <button ref={closeButton} type="button" aria-label="Close" onClick={close} className={controlClass}>
            <X size={20} aria-hidden />
          </button>
          <h1 className="min-w-0 flex-1 truncate text-[15px] font-medium" title={path}>
            {name}
          </h1>
          <button type="button" aria-label="Info" aria-expanded={showInfo} onClick={() => setShowInfo(!showInfo)} className={controlClass}>
            <Info size={20} aria-hidden />
          </button>
          <Menu label={`Actions for ${name}`} items={actions} placement="below" className={controlClass}>
            <Ellipsis size={20} aria-hidden />
          </Menu>
        </div>
        <div className="flex justify-end">
          {kind === "pdf" && pdf ? (
            <span className={badgeClass}>
              Page {pdf.page} / {pdf.pages}
              {pdf.zoom > 1 && ` · ${Math.round(pdf.zoom * 100)} %`}
            </span>
          ) : (
            zoom > 1.01 && <span className={badgeClass}>{Math.round(zoom * 100)} %</span>
          )}
        </div>
        {showInfo && (
          <dl aria-label="Info" className="pointer-events-auto ml-auto grid w-full max-w-xs grid-cols-[auto_1fr] gap-x-3 gap-y-1 rounded-[12px] bg-[#1a1a1ee6] p-3 text-[13px]">
            <InfoRow label="Folder">{folderOf(path).slice(1).split("/").join(" / ")}</InfoRow>
            {extent && <InfoRow label={kind === "pdf" ? "Pages" : "Dimensions"}>{extent}</InfoRow>}
            {info.data && (
              <>
                <InfoRow label="Size">{sizeText(Number(info.data.size))}</InfoRow>
                <InfoRow label="Date">{timeText(info.data.lastWriteAt)}</InfoRow>
                <InfoRow label="Writer">{info.data.lastWriter}</InfoRow>
              </>
            )}
            {info.isError && <dd className="col-span-2 text-[#a09d96]">The rest couldn't be loaded.</dd>}
          </dl>
        )}
      </div>

      {previous && (
        <button type="button" aria-label="Previous" onClick={() => show(previous)} className={`${controlClass} absolute top-1/2 left-2 hidden -translate-y-1/2 sm:flex`}>
          <ChevronLeft size={22} aria-hidden />
        </button>
      )}
      {next && (
        <button type="button" aria-label="Next" onClick={() => show(next)} className={`${controlClass} absolute top-1/2 right-2 hidden -translate-y-1/2 sm:flex`}>
          <ChevronRight size={22} aria-hidden />
        </button>
      )}

      <div className="pointer-events-none absolute inset-x-0 bottom-0 flex flex-col items-center gap-1.5 bg-gradient-to-t from-black/80 to-transparent px-2 pt-6 pb-[max(10px,env(safe-area-inset-bottom))]">
        {copy && (
          <p role="status" className="text-[12px] text-[#a09d96]">
            {copy === "copied" ? "Link copied. Paste it into a note." : "Couldn't copy the link."}
          </p>
        )}
        {paths.length > 1 && at >= 0 && (
          <p className="text-[11px] text-[#8f8c86]">
            {at + 1} / {paths.length}
          </p>
        )}
        <div className="pointer-events-auto flex gap-2">
          <a ref={download} href={downloadUrl} download={name} aria-label="Download" className={controlClass}>
            <Download size={20} aria-hidden />
          </a>
          <button type="button" aria-label="Rename or move" onClick={() => setDialog("move")} className={controlClass}>
            <FolderInput size={20} aria-hidden />
          </button>
          <button type="button" aria-label="Copy link" onClick={() => void copyLink()} className={controlClass}>
            <Link2 size={20} aria-hidden />
          </button>
          <button type="button" aria-label="Delete" disabled={!version} onClick={() => setDialog("delete")} className={controlClass}>
            <Trash2 size={20} aria-hidden />
          </button>
        </div>
      </div>

      {dialog === "move" && (
        <MoveDialog
          subject={{ kind: "attachment", path }}
          onClose={() => setDialog(null)}
          onMoved={(to) => show(to, paths.map((p) => (p === path ? to : p)))}
        />
      )}
      {dialog === "delete" && version && (
        <DeleteFileDialog
          path={path}
          version={version}
          onClose={() => setDialog(null)}
          onDeleted={() => {
            setDialog(null);
            const rest = paths.filter((p) => p !== path);
            const then = next ?? previous;
            if (then) show(then, rest);
            else close();
          }}
        />
      )}
    </main>
  );
}

function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-[#a09d96]">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </>
  );
}

/**
 * An image, fitted to the screen: pinch, wheel and double tap zoom it, a finger or the mouse moves it while zoomed,
 * and without zoom a swipe pages or closes (`useZoomPan`).
 */
function ImageStage({ vault, path, name, onSwipe, onZoom }: { vault: string; path: string; name: string; onSwipe(to: "next" | "previous" | "close"): void; onZoom(scale: number): void }) {
  const url = useAttachmentUrl(vault, inVault(path));
  const stage = useRef<HTMLDivElement>(null);
  const [natural, setNatural] = useState<{ width: number; height: number } | null>(null);
  const [broken, setBroken] = useState(false);
  const { view, offset, handlers } = useZoomPan(stage, natural, onSwipe);
  const zoomed = useRef(onZoom);
  zoomed.current = onZoom;

  useEffect(() => zoomed.current(view.scale), [view.scale]);

  return (
    <div ref={stage} data-testid="image-stage" className="absolute inset-0 touch-none overflow-hidden select-none" {...handlers}>
      {url === null || broken ? (
        <p role="alert" className="absolute inset-x-6 top-1/2 -translate-y-1/2 text-center text-[#d9d6cf]">
          This image can't be shown. Check the connection and try again.
        </p>
      ) : url === undefined ? (
        <p className="absolute inset-x-6 top-1/2 -translate-y-1/2 text-center text-[#a09d96]">Loading …</p>
      ) : (
        <img
          src={url}
          alt={name}
          draggable={false}
          onLoad={(e) => setNatural({ width: e.currentTarget.naturalWidth, height: e.currentTarget.naturalHeight })}
          onError={() => setBroken(true)}
          className="absolute inset-0 size-full object-contain [-webkit-touch-callout:none]"
          style={{
            transform: `translate(${view.x + offset.x}px, ${view.y + offset.y}px) scale(${view.scale})`,
            opacity: 1 - Math.min(0.5, offset.y / 400),
          }}
        />
      )}
    </div>
  );
}
