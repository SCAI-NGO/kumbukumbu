// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft, Check, Download, FileText, Folder, FolderInput, Image as ImageIcon, ListChecks, Trash2, Upload, X } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, memo, useCallback, useContext, useEffect, useId, useMemo, useRef, useState, type MouseEvent, type ReactNode, type RefObject } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api } from "../api/client";
import { errorText } from "../api/error-text";
import { useAttachmentUrl, useDeleteFile, useTree, type TreeFolder } from "../api/notes";
import { fileKindOf, folderOf, inVault, titleOf, viewUrl, type LightboxState } from "../api/paths";
import { folderOrder, textOrder } from "../api/textOrder";
import ConfirmTyped from "../components/ConfirmTyped";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { sizeText } from "../components/Sheet";
import { topDialog, useDialog } from "../hooks/useDialog";
import MoveDialog from "../shell/MoveDialog";
import { useEditor } from "../state/editor";
import DeleteFileDialog from "./DeleteFileDialog";
import { downloadFile, downloadZip, maxZipFiles, preferencesQuery, zipFrameError } from "./downloadFiles";
import MoveFilesDialog from "./MoveFilesDialog";
import { openPdf } from "./pdf";
import { useLongPress, useSelection } from "./useSelection";

export type GalleryOrder = "date" | "folder" | "name";

const orders: { id: GalleryOrder; label: string }[] = [
  { id: "date", label: "Date" },
  { id: "folder", label: "Folder" },
  { id: "name", label: "Name" },
];

const orderKey = "kumbu.galleryOrder";

/** The order this device chose last, else by date; storage may be blocked, so every access is guarded. */
function storedOrder(): GalleryOrder {
  try {
    const stored = localStorage.getItem(orderKey);
    return orders.some((o) => o.id === stored) ? (stored as GalleryOrder) : "date";
  } catch {
    return "date";
  }
}

function storeOrder(order: GalleryOrder) {
  try {
    localStorage.setItem(orderKey, order);
  } catch {
    // The order still applies for this visit.
  }
}

/** An image or PDF of the vault as the tree lists it. */
export interface GalleryFile {
  path: string;
  name: string;
  size: number;
  version?: string;
  writtenAt?: string;
}

/** Every image and PDF in the folder and below it. */
function filesOf(folder: TreeFolder): GalleryFile[] {
  const here = folder.notes
    .filter((n) => fileKindOf(n.path) !== "note")
    .map((n) => ({ path: n.path, name: n.name, size: Number(n.size), version: n.version ?? undefined, writtenAt: n.writtenAt ?? undefined }));
  return [...here, ...folder.folders.flatMap(filesOf)];
}

/** By name in the tree's text order, the same name in two folders by the folders'. */
const byName = (a: GalleryFile, b: GalleryFile) => textOrder(a.name, b.name) || folderOrder(folderOf(a.path), folderOf(b.path));

/** A section of the gallery: a day, a folder, or (by name) the one untitled list. */
export interface GalleryGroup {
  label: string;
  files: GalleryFile[];
}

const dayKey = (d: Date) => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;

function dayLabel(d: Date, now: Date) {
  if (dayKey(d) === dayKey(now)) return "Today";
  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  if (dayKey(d) === dayKey(yesterday)) return "Yesterday";
  return d.toLocaleDateString("en-GB", { dateStyle: "medium" });
}

/** `/Work/Projects/Teaser` → `/Projects/Teaser`, the vault's top level as `/`. */
const folderLabel = (folder: string) => (folder.indexOf("/", 1) < 0 ? "/" : folder.slice(folder.indexOf("/", 1)));

/**
 * The gallery's sections in an order (attachments design, "Gallery"): by date, newest first, a section a day (the
 * reader's day); by folder, sections A–Z and the files A–Z within; by name, one flat list A–Z across folders.
 */
export function arrange(files: GalleryFile[], order: GalleryOrder, now = new Date()): GalleryGroup[] {
  if (order === "name") return files.length ? [{ label: "", files: [...files].sort(byName) }] : [];
  if (order === "folder") {
    const folders = new Map<string, GalleryFile[]>();
    for (const file of files) folders.set(folderOf(file.path), [...(folders.get(folderOf(file.path)) ?? []), file]);
    return [...folders]
      .sort(([a], [b]) => folderOrder(a, b))
      .map(([folder, list]) => ({ label: folderLabel(folder), files: list.sort(byName) }));
  }
  const time = (f: GalleryFile) => (f.writtenAt ? Date.parse(f.writtenAt) : 0);
  const groups: GalleryGroup[] = [];
  for (const file of [...files].sort((a, b) => time(b) - time(a) || byName(a, b))) {
    const label = file.writtenAt ? dayLabel(new Date(file.writtenAt), now) : "Earlier";
    if (groups.at(-1)?.label === label) groups.at(-1)!.files.push(file);
    else groups.push({ label, files: [file] });
  }
  return groups;
}

/**
 * `/gallery/<Vault>`: the vault's images and PDFs as tiles (attachments design, "Gallery"), ordered by date, folder
 * or name (the device remembers which). A tap opens the lightbox, which pages through the gallery's order; a long
 * press (or "Select", or Ctrl-click) selects, and a bar offers download, move and delete for what is selected.
 */
export default function GalleryPage() {
  const { vault = "" } = useParams();
  const navigate = useNavigate();
  const tree = useTree(vault);
  const [order, setOrder] = useState(storedOrder);
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null);
  const selection = useSelection();
  const { selected, selecting, toggle, clear, keep, drop } = selection;
  // A dialog keeps the files it was opened for: the selection changes under it as they move or go.
  const [dialog, setDialog] = useState<{ kind: "download" | "move" | "delete"; files: GalleryFile[] } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const files = useMemo(() => (tree.data ? filesOf(tree.data.root) : []), [tree.data]);
  const groups = useMemo(() => arrange(files, order), [files, order]);
  const paths = useMemo(() => groups.flatMap((g) => g.files.map((f) => f.path)), [groups]);
  const byPath = useMemo(() => new Map(files.map((f) => [f.path, f])), [files]);
  const chosen = selected.flatMap((p) => byPath.get(p) ?? []);
  const total = chosen.reduce((sum, f) => sum + f.size, 0);

  // A file moved or deleted (here or elsewhere) leaves the selection.
  useEffect(() => {
    if (tree.data) keep(new Set(byPath.keys()));
  }, [tree.data, byPath, keep]);

  // Escape ends selecting, unless a dialog has the keyboard.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Escape" || topDialog() || e.defaultPrevented) return;
      clear();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [clear]);

  const longPress = useLongPress(toggle);

  const choose = (next: GalleryOrder) => {
    setOrder(next);
    storeOrder(next);
  };

  const { fired } = longPress;
  const onTile = useCallback(
    (path: string, e: MouseEvent) => {
      if (fired()) return;
      if (selecting || e.ctrlKey || e.metaKey) return toggle(path);
      navigate(viewUrl(path), { state: { paths } satisfies LightboxState });
    },
    [fired, selecting, toggle, navigate, paths],
  );

  // The upload sheet lives with the notes: it opens there, for the vault's top level.
  const upload = () => {
    useEditor.getState().setActiveVault(vault);
    useEditor.getState().openUpload(`/${vault}`);
    navigate("/");
  };

  const download = async () => {
    setProblem(null);
    if (chosen.length === 1) return downloadFile(chosen[0].path);
    const files = chosen;
    // Asked afresh: a setting turned back on elsewhere must not leave a ZIP without its ticket. Unknown, it asks.
    const ask = await queryClient
      .fetchQuery({ ...preferencesQuery, staleTime: 0 })
      .then((p) => p.askPasswordForDownloads)
      .catch(() => true);
    if (ask) setDialog({ kind: "download", files });
    else void downloadZip(zipFrame, vault, files.map((f) => f.path));
  };

  const versioned = chosen.every((f) => f.version);

  return (
    <div className="relative flex h-dvh flex-col bg-bg">
      <header className="flex flex-none items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        <Link to="/" aria-label="Back to the notes" className={iconButtonClass}>
          <ArrowLeft size={20} aria-hidden />
        </Link>
        <h1 className="min-w-0 flex-1 truncate px-2 text-[20px] font-bold text-text">Gallery · {vault}</h1>
        {files.length > 0 && (
          <button type="button" aria-pressed={selecting} onClick={() => (selecting ? clear() : selection.start())} className={`${iconButtonClass} gap-1.5 px-3 text-sm`}>
            <ListChecks size={18} aria-hidden />
            {selecting ? "Done" : "Select"}
          </button>
        )}
        <button type="button" aria-label="Upload" onClick={upload} className={iconButtonClass}>
          <Upload size={20} aria-hidden />
        </button>
      </header>

      <div role="radiogroup" aria-label="Order" className="mx-3 mt-2.5 mb-1 flex flex-none rounded-[9px] border border-line bg-surface p-0.5 sm:max-w-sm">
        {orders.map((o) => (
          <label
            key={o.id}
            className={`flex min-h-9 flex-1 cursor-pointer items-center justify-center rounded-[7px] text-[13px] has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-accent ${
              order === o.id ? "bg-accent font-semibold text-on-accent" : "text-text-body"
            }`}
          >
            <input type="radio" name="gallery-order" value={o.id} checked={order === o.id} onChange={() => choose(o.id)} className="sr-only" />
            {o.label}
          </label>
        ))}
      </div>

      <div ref={setScroller} data-testid="gallery" className={`min-h-0 flex-1 overflow-y-auto px-3 pt-1 ${selecting ? "pb-28" : "pb-6"}`}>
        {tree.isPending && <p className="py-3 text-sm text-muted">Loading …</p>}
        {tree.isError && <p className="py-3 text-text">{errorText(tree.error)}</p>}
        {tree.data && files.length === 0 && (
          <div className="mx-auto flex max-w-sm flex-col items-center gap-3 py-16 text-center">
            <ImageIcon size={36} aria-hidden className="text-dim" />
            <p className="text-lg font-bold text-text">No images or PDFs in {vault} yet.</p>
            <p className="text-text-body">Upload screenshots, photos or PDFs, and they show here.</p>
            <button type="button" onClick={upload} className={primaryButtonClass}>
              Upload files
            </button>
          </div>
        )}
        <ScrollRoot.Provider value={scroller}>
        {groups.map((group) =>
          group.label ? (
            <section key={group.label} aria-label={group.label} className="mb-2">
              {order === "folder" ? (
                <h2 className="flex items-center gap-1.5 px-1 pt-2.5 pb-1.5 text-[14px] text-text-body">
                  <Folder size={15} aria-hidden className="text-dim" />
                  <span className="min-w-0 truncate">{group.label}</span>
                  <small className="ml-auto text-[12px] text-dim">{group.files.length}</small>
                </h2>
              ) : (
                <h2 className="px-1 pt-2.5 pb-1.5 font-mono text-[11px] tracking-[0.06em] text-dim uppercase">{group.label}</h2>
              )}
              <Tiles vault={vault} files={group.files} selecting={selecting} selected={selected} onTile={onTile} longPress={longPress.handlers} />
            </section>
          ) : (
            <div key="all" className="pt-1.5">
              <Tiles vault={vault} files={group.files} selecting={selecting} selected={selected} onTile={onTile} longPress={longPress.handlers} />
            </div>
          ),
        )}
        </ScrollRoot.Provider>
      </div>

      {selecting && (
        <section
          aria-label="Selection"
          className="absolute inset-x-0 bottom-0 z-20 flex flex-col gap-1.5 border-t border-line bg-surface px-3 pt-2 pb-[max(10px,env(safe-area-inset-bottom))]"
        >
          {problem && <FormError>{problem}</FormError>}
          {chosen.length > maxZipFiles && <p className="text-sm text-muted">A download takes at most {maxZipFiles} files at once.</p>}
          <div className="flex items-center gap-1.5">
            <button type="button" aria-label="Clear selection" onClick={clear} className={iconButtonClass}>
              <X size={20} aria-hidden />
            </button>
            <p className="min-w-0 flex-1 truncate text-sm text-text">{chosen.length === 0 ? "Tap files to select them" : `${chosen.length} selected · ${sizeText(total)}`}</p>
            <BarButton label="Download" disabled={chosen.length === 0 || chosen.length > maxZipFiles} onClick={() => void download()}>
              <Download size={20} aria-hidden />
            </BarButton>
            <BarButton label="Move" disabled={chosen.length === 0} onClick={() => setDialog({ kind: "move", files: chosen })}>
              <FolderInput size={20} aria-hidden />
            </BarButton>
            <BarButton label="Delete" disabled={chosen.length === 0 || !versioned} onClick={() => setDialog({ kind: "delete", files: chosen })}>
              <Trash2 size={20} aria-hidden />
            </BarButton>
          </div>
        </section>
      )}

      {problem && !selecting && (
        <div className="absolute inset-x-3 bottom-3 z-20">
          <FormError>{problem}</FormError>
        </div>
      )}
      <ZipFrame onError={setProblem} />

      {dialog?.kind === "download" && <ZipDialog vault={vault} paths={dialog.files.map((f) => f.path)} onClose={() => setDialog(null)} />}
      {dialog?.kind === "move" &&
        (dialog.files.length === 1 ? (
          <MoveDialog subject={{ kind: "attachment", path: dialog.files[0].path }} onClose={() => setDialog(null)} onMoved={clear} />
        ) : (
          <MoveFilesDialog paths={dialog.files.map((f) => f.path)} onClose={() => setDialog(null)} onMoved={drop} />
        ))}
      {dialog?.kind === "delete" &&
        (dialog.files.length === 1 ? (
          <DeleteFileDialog
            path={dialog.files[0].path}
            version={dialog.files[0].version!}
            onClose={() => setDialog(null)}
            onDeleted={() => {
              setDialog(null);
              clear();
            }}
          />
        ) : (
          <DeleteFilesDialog
            files={dialog.files}
            onClose={() => setDialog(null)}
            onDeleted={() => {
              setDialog(null);
              clear();
            }}
          />
        ))}
    </div>
  );
}

const iconButtonClass =
  "flex min-h-11 min-w-11 flex-none items-center justify-center rounded-[10px] text-text-body disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

function BarButton({ label, disabled, onClick, children }: { label: string; disabled: boolean; onClick(): void; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} title={label} disabled={disabled} onClick={onClick} className={`${iconButtonClass} bg-surface-2`}>
      {children}
    </button>
  );
}

type LongPressHandlers = ReturnType<typeof useLongPress>["handlers"];

function Tiles(props: {
  vault: string;
  files: GalleryFile[];
  selecting: boolean;
  selected: string[];
  onTile(path: string, e: MouseEvent): void;
  longPress: LongPressHandlers;
}) {
  return (
    <div className="grid grid-cols-3 gap-1 sm:grid-cols-[repeat(auto-fill,minmax(150px,1fr))]">
      {props.files.map((file) => (
        <Tile
          key={file.path}
          vault={props.vault}
          file={file}
          selecting={props.selecting}
          isSelected={props.selected.includes(file.path)}
          onTile={props.onTile}
          longPress={props.longPress}
        />
      ))}
    </div>
  );
}

/** The gallery's scrolling list, which the tiles are near to or far from (they scroll inside it, not the page). */
const ScrollRoot = createContext<HTMLElement | null>(null);

/**
 * Within about a screen's height of the gallery's visible part (above or below): only then does a tile hold its
 * picture. A screen ahead loads before it is reached; further away the picture goes again, which bounds what a phone
 * holds (iOS Safari above all) at the price of fetching it anew on the way back.
 */
function useNear(element: RefObject<HTMLElement>) {
  const root = useContext(ScrollRoot);
  const [near, setNear] = useState(typeof IntersectionObserver === "undefined");
  useEffect(() => {
    const target = element.current;
    if (!target || !root || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(([entry]) => setNear(entry.isIntersecting), { root, rootMargin: "100% 0px" });
    observer.observe(target);
    return () => observer.disconnect();
  }, [element, root]);
  return near;
}

const Tile = memo(function Tile({
  vault,
  file,
  selecting,
  isSelected,
  onTile,
  longPress,
}: {
  vault: string;
  file: GalleryFile;
  selecting: boolean;
  isSelected: boolean;
  onTile(path: string, e: MouseEvent): void;
  longPress: LongPressHandlers;
}) {
  const box = useRef<HTMLButtonElement>(null);
  const near = useNear(box);
  const pdf = fileKindOf(file.path) === "pdf";
  return (
    <button
      ref={box}
      type="button"
      aria-label={file.name}
      aria-pressed={selecting ? isSelected : undefined}
      onClick={(e) => onTile(file.path, e)}
      {...longPress(file.path)}
      className={`relative aspect-square overflow-hidden rounded-[6px] bg-surface-2 select-none [-webkit-touch-callout:none] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent ${
        isSelected ? "ring-3 ring-accent ring-inset" : ""
      }`}
    >
      {/* Far from the screen a tile holds nothing: its image's object URL is revoked, its PDF canvas emptied. */}
      {near && (pdf ? <PdfTile vault={vault} path={file.path} version={file.version} /> : <ImageTile vault={vault} path={file.path} version={file.version} />)}
      <span className="pointer-events-none absolute inset-x-0 bottom-0 truncate bg-gradient-to-t from-black/60 to-transparent px-1.5 pt-3 pb-0.5 text-left text-[10px] text-white">
        {file.name}
      </span>
      {selecting && (
        <span
          aria-hidden
          className={`absolute top-1.5 right-1.5 flex size-6 items-center justify-center rounded-full border-2 ${isSelected ? "border-accent bg-accent text-on-accent" : "border-white/80 bg-black/30"}`}
        >
          {isSelected && <Check size={14} strokeWidth={3} />}
        </span>
      )}
    </button>
  );
});

function Placeholder({ pdf }: { pdf: boolean }) {
  const Icon = pdf ? FileText : ImageIcon;
  return <Icon size={28} aria-hidden className="absolute inset-0 m-auto text-dim" />;
}

/** The image itself, scaled by the browser (there are no thumbnails on the server in this part). */
function ImageTile({ vault, path, version }: { vault: string; path: string; version?: string }) {
  // At the version the tree lists: a file changed meanwhile is fetched anew, not shown as it was.
  const url = useAttachmentUrl(vault, inVault(path), version);
  const [broken, setBroken] = useState(false);
  if (!url || broken) return <Placeholder pdf={false} />;
  return <img src={url} alt="" draggable={false} decoding="async" onError={() => setBroken(true)} className="absolute inset-0 size-full object-cover" />;
}

/** A canvas never larger than this many pixels, whatever the screen's density. */
const maxTilePixels = 1_000_000;

/**
 * A PDF's first page at the tile's size, drawn by pdf.js; the document is let go of as soon as it is drawn, and the
 * canvas emptied when the tile leaves (browsers cap what all canvases together may hold, iOS Safari above all).
 */
function PdfTile({ vault, path, version }: { vault: string; path: string; version?: string }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [drawn, setDrawn] = useState(false);

  useEffect(() => {
    let task: Awaited<ReturnType<typeof openPdf>> | undefined;
    let render: { cancel(): void } | undefined;
    let ended = false;
    const controller = new AbortController();
    const target = canvas.current;
    void (async () => {
      try {
        const { data, response } = await api.GET("/api/files/{vault}/{path}", {
          params: { path: { vault, path: inVault(path) }, query: version ? { version } : {} },
          parseAs: "arrayBuffer",
          signal: controller.signal,
        });
        if (ended || !response.ok || !data || !target) return;
        task = await openPdf(data);
        if (ended) return;
        const page = await (await task.promise).getPage(1);
        if (ended) return;
        const base = page.getViewport({ scale: 1 });
        const wanted = ((target.clientWidth || 160) / base.width) * (window.devicePixelRatio || 1);
        const viewport = page.getViewport({ scale: Math.min(wanted, Math.sqrt(maxTilePixels / (base.width * base.height))) });
        target.width = Math.floor(viewport.width);
        target.height = Math.floor(viewport.height);
        const drawing = page.render({ canvas: target, viewport });
        render = drawing;
        await drawing.promise;
        if (!ended) setDrawn(true);
      } catch {
        // Not readable, not a PDF pdf.js opens, or let go of for another: the tile keeps its icon.
      } finally {
        // Drawn (or not): the document and its worker go; the canvas keeps the pixels.
        void task?.destroy();
        task = undefined;
      }
    })();
    return () => {
      ended = true;
      controller.abort();
      render?.cancel();
      void task?.destroy();
      if (target) {
        target.width = 0;
        target.height = 0;
      }
    };
  }, [vault, path, version]);

  return (
    <>
      {!drawn && <Placeholder pdf />}
      <canvas ref={canvas} aria-hidden className="absolute inset-0 size-full bg-white object-cover object-top" style={{ visibility: drawn ? "visible" : "hidden" }} />
    </>
  );
}

/** The frame the ZIP's form posts into (`postZip`); it stays out of sight and out of the tab order. */
const zipFrame = "kumbu-zip";

/**
 * The hidden frame a ZIP is posted into. A ZIP goes to disk and leaves it as it is; an error answer (the server's
 * JSON) loads into it, is read (it is of the same origin, which the route lets frame it), handed on, and the frame
 * emptied again.
 */
function ZipFrame({ onError }: { onError(message: string): void }) {
  return (
    <iframe
      name={zipFrame}
      title="Downloads"
      hidden
      tabIndex={-1}
      onLoad={(e) => {
        const frame = e.currentTarget;
        const error = zipFrameError(frame);
        if (error === null) return;
        onError(error);
        frame.src = "about:blank";
      }}
    />
  );
}

/** Several files as one ZIP after the password (unless the user turned that off in Settings). */
function ZipDialog({ vault, paths, onClose }: { vault: string; paths: string[]; onClose(): void }) {
  const [password, setPassword] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <ConfirmTyped
      title={`Download ${paths.length} files?`}
      text="They come as one ZIP. Your password confirms it; Settings can turn this question off."
      action="Download"
      pending={pending}
      error={error}
      password={password}
      onPasswordChange={setPassword}
      onConfirm={async () => {
        setPending(true);
        setError(null);
        try {
          await downloadZip(zipFrame, vault, paths, password);
          onClose();
        } catch (e) {
          setError(errorText(e));
        } finally {
          setPending(false);
        }
      }}
      onClose={onClose}
    />
  );
}

/**
 * Asks before several images and PDFs go to the trash, then sends each at the version the gallery shows (one that
 * changed meanwhile stays, and says so). A refusal on the way stops there; what went is out of the selection.
 */
function DeleteFilesDialog({ files, onDeleted, onClose }: { files: GalleryFile[]; onDeleted(): void; onClose(): void }) {
  const id = useId();
  const [pending, setPending] = useState(false);
  // Not closed while files are going: what went and what didn't would be lost.
  const dialog = useDialog<HTMLDivElement>(() => !pending && onClose());
  const remove = useDeleteFile();
  const [problem, setProblem] = useState<string | null>(null);
  // After a refusal, what is left to go.
  const [list, setList] = useState(files);

  const confirm = async () => {
    setPending(true);
    setProblem(null);
    let gone = 0;
    try {
      for (const file of list) {
        await remove.mutateAsync({ path: file.path, version: file.version! });
        gone++;
      }
      onDeleted();
    } catch (e) {
      setProblem(`${gone > 0 ? `${gone} of ${list.length} went to the trash. ` : ""}${titleOf(list[gone].path)}: ${errorText(e)}`);
      setList(list.slice(gone));
    } finally {
      setPending(false);
    }
  };

  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby={`${id}-title`} className="k-sheet flex w-full max-w-md flex-col gap-3 rounded-[16px] border border-line bg-surface p-[18px]">
        <h2 id={`${id}-title`} className="text-[20px] font-bold text-text">
          Delete {list.length === 1 ? titleOf(list[0].path) : `${list.length} files`}?
        </h2>
        <p className="text-text-body">They go to the vault's trash. Links to them in notes lead nowhere until they are restored.</p>
        {problem && <FormError>{problem}</FormError>}
        <div className="mt-1 flex gap-2.5">
          <button type="button" data-autofocus disabled={pending} onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={pending} onClick={() => void confirm()} className={`${primaryButtonClass} flex-1`}>
            Delete
          </button>
        </div>
      </div>
    </div>
  );
}
