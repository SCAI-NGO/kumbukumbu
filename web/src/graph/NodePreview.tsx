// SPDX-License-Identifier: AGPL-3.0-or-later
import { ago } from "../note/AgentBanner";
import { useGraphNode } from "./api";
import type { GNode } from "./model";

const WIDTH = 256;
const GAP = 12;

/**
 * The small card over the graph for a hovered or focused node, at `anchor` (the column's coordinates): its title,
 * tags, first paragraph and when it changed, asked for while it is shown; a ghost's says "No note yet" and asks nothing.
 * It takes no pointer, or it would end the hover that shows it.
 */
export default function NodePreview({ node, anchor }: { node: GNode; anchor: { x: number; y: number } }) {
  const detail = useGraphNode(node.path, true).data;
  return (
    <div
      role="tooltip"
      style={{ left: Math.max(GAP, Math.min(anchor.x + GAP, 560 - WIDTH - GAP)), top: anchor.y + GAP, width: WIDTH }}
      className="pointer-events-none absolute z-10 flex flex-col gap-1 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm shadow-lg"
    >
      <p className="truncate font-medium text-text">{node.title}</p>
      {node.kind === "ghost" ? (
        <p className="text-muted">No note yet</p>
      ) : (
        <>
          {node.tags.length > 0 && <p className="truncate font-mono text-[11px] text-dim">{node.tags.map((t) => `#${t}`).join(" ")}</p>}
          {detail?.firstParagraph && <p className="line-clamp-4 text-muted">{detail.firstParagraph}</p>}
          {detail?.changed && <p className="text-[12px] text-dim">changed {ago(detail.changed)}</p>}
        </>
      )}
    </div>
  );
}
