// SPDX-License-Identifier: AGPL-3.0-or-later
import type { Ref } from "react";
import type { GNode } from "./model";
import { cardPlace, PreviewBody } from "./NodePreview";

const button =
  "min-h-9 rounded-md border border-line bg-surface px-3 text-sm text-text focus-visible:outline-2 focus-visible:outline-accent";

/**
 * The card of the node selected in the dialog, at `anchor` in the coordinates of a body `width` wide: the preview's
 * content and Open, with `beside` also Open beside; a ghost has nothing to open. Escape is the graph's to handle.
 */
export default function NodeCard({ ref, node, anchor, width, beside, onOpen }: { ref?: Ref<HTMLDivElement>; node: GNode; anchor: { x: number; y: number }; width: number; beside: boolean; onOpen(node: GNode, beside: boolean): void }) {
  return (
    <div ref={ref} role="group" aria-label={node.title} style={cardPlace(anchor, width)} className="absolute z-10 flex flex-col gap-1 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm shadow-lg">
      <PreviewBody node={node} />
      {node.kind !== "ghost" && (
        <div className="mt-1 flex gap-2">
          <button type="button" onClick={() => onOpen(node, false)} className={button}>
            Open
          </button>
          {beside && (
            <button type="button" onClick={() => onOpen(node, true)} className={button}>
              Open beside
            </button>
          )}
        </div>
      )}
    </div>
  );
}
