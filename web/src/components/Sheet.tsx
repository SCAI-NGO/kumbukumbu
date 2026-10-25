// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft, X } from "lucide-react";
import { useId, type ReactNode } from "react";
import { useDialog } from "../hooks/useDialog";

/**
 * A sheet from the bottom (centred on wide screens) with a title, a close button and, inside a step of it, a way
 * back. Escape and a tap beside it close it, as the outline's.
 */
export function Sheet({ title, onClose, onBack, backLabel, children }: { title: string; onClose(): void; onBack?(): void; backLabel?: string; children: ReactNode }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onBack ?? onClose);
  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 sm:items-center" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-[85dvh] w-full max-w-xl flex-col rounded-t-[16px] border border-line bg-surface pb-[max(16px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <div className="flex flex-none items-center gap-1 py-2 pr-2 pl-[18px]">
          {onBack && (
            <button
              type="button"
              aria-label={backLabel ?? "Back"}
              onClick={onBack}
              className="-ml-3 flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
            >
              <ArrowLeft size={20} aria-hidden />
            </button>
          )}
          <h2 id={`${id}-title`} tabIndex={-1} data-autofocus className="min-w-0 flex-1 truncate text-[20px] font-bold text-text focus:outline-none">
            {title}
          </h2>
          <button
            type="button"
            aria-label={`Close ${title.toLowerCase()}`}
            onClick={onClose}
            className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
          >
            <X size={22} aria-hidden />
          </button>
        </div>
        <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto px-[18px] pb-1">{children}</div>
      </div>
    </div>
  );
}

/** "312 B", "4.2 KB", "1.1 MB": a note's size in bytes, for a list. */
export function sizeText(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** "25 Oct 2026, 09:00": a moment in the reader's time zone. */
export const timeText = (iso: string) => new Date(iso).toLocaleString("en-GB", { dateStyle: "medium", timeStyle: "short" });
