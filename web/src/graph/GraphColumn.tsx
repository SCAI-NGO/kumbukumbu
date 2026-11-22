// SPDX-License-Identifier: AGPL-3.0-or-later
import GraphView from "./GraphView";

/** The column's id, for its toggle in the top bar. */
export const graphColumnId = "graph-column";

/** Beside the note on the widest windows: the graph of `path`, its simulation stopped while `hidden`. */
export default function GraphColumn({ path, otherPath, hidden }: { path: string; otherPath: string | null; hidden: boolean }) {
  return (
    <aside id={graphColumnId} aria-label="Graph" hidden={hidden} className="flex w-[560px] flex-none flex-col border-l border-line bg-surface">
      <GraphView path={path} otherPath={otherPath} running={!hidden} mode="column" />
    </aside>
  );
}
