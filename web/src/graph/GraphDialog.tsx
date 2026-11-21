// SPDX-License-Identifier: AGPL-3.0-or-later
import { X } from "lucide-react";
import { useId } from "react";
import { titleOf } from "../api/paths";
import { useDialog } from "../hooks/useDialog";
import GraphView from "./GraphView";

/**
 * Where no column fits: the graph of `path` over the shell, full screen on a phone and a large centred panel from `sm`
 * up. Escape, its close button and a tap beside it close it, as our sheets do; opening a node closes it too.
 */
export default function GraphDialog({ path, otherPath, onClose }: { path: string; otherPath: string | null; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  return (
    <div className="fixed inset-0 z-30 flex items-center justify-center sm:bg-black/55" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex size-full flex-col bg-surface pb-[env(safe-area-inset-bottom)] sm:h-[min(85dvh,800px)] sm:w-[min(90vw,960px)] sm:rounded-[16px] sm:border sm:border-line sm:pb-0"
      >
        <div className="flex flex-none items-center gap-1 border-b border-line py-2 pr-2 pl-[18px]">
          <div className="min-w-0 flex-1">
            <h2 id={`${id}-title`} tabIndex={-1} data-autofocus className="truncate text-[20px] font-bold text-text focus:outline-none">
              {titleOf(path)}
            </h2>
            <p className="text-[12px] text-dim">Graph</p>
          </div>
          <button
            type="button"
            aria-label="Close graph"
            onClick={onClose}
            className="flex size-11 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent"
          >
            <X size={22} aria-hidden />
          </button>
        </div>
        <GraphView mode="dialog" running path={path} otherPath={otherPath} onOpened={onClose} />
      </div>
    </div>
  );
}
