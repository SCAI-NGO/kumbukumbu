// SPDX-License-Identifier: AGPL-3.0-or-later
import { useDeferredValue, useId, useMemo, useRef, type KeyboardEvent } from "react";
import { useSidePanelTab, type SidePanelTab } from "../hooks/useSidePanel";
import BacklinksList from "./BacklinksList";
import { OutlineList } from "./NoteTools";
import { outline } from "./outline";
import { splitFrontmatter } from "./properties";

const tabs: { id: SidePanelTab; label: string }[] = [
  { id: "links", label: "Links" },
  { id: "outline", label: "Outline" },
];

const tabClass = (on: boolean) =>
  `flex min-h-11 flex-1 items-center justify-center rounded-[10px] px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent text-on-accent" : "text-text-body"}`;

/**
 * Beside the note on wide windows: its links (what links here, unlinked mentions) and its outline, a tab each, the
 * last one chosen remembered. `text` is the note as it stands (the editor's while writing), `currentLine` the
 * 1-based source line the reader is at: the last heading at or above it is marked.
 */
export default function SidePanel({ path, text, currentLine, onJump }: { path: string; text: string; currentLine: number; onJump(line: number): void }) {
  const id = useId();
  const [tab, setTab] = useSidePanelTab();
  const refs = useRef<Partial<Record<SidePanelTab, HTMLButtonElement | null>>>({});
  // Deferred, so a long note's outline never holds up typing.
  const deferred = useDeferredValue(text);
  const headings = useMemo(() => (tab === "outline" ? outline(splitFrontmatter(deferred).body) : []), [tab, deferred]);
  const current = headings.findLast((h) => h.line <= currentLine)?.line;

  const index = tabs.findIndex((t) => t.id === tab);
  const onKeyDown = (e: KeyboardEvent) => {
    const to = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: tabs.length - 1 }[e.key];
    if (to === undefined) return;
    e.preventDefault();
    const next = tabs[(to + tabs.length) % tabs.length];
    setTab(next.id);
    refs.current[next.id]?.focus();
  };

  return (
    <aside aria-label="Side panel" className="flex w-[300px] flex-none flex-col border-l border-line bg-surface">
      <div role="tablist" aria-label="Side panel" onKeyDown={onKeyDown} className="m-3 flex flex-none gap-1 rounded-[12px] border border-line p-1">
        {tabs.map((t) => (
          <button
            key={t.id}
            ref={(el) => {
              refs.current[t.id] = el;
            }}
            type="button"
            role="tab"
            id={`${id}-tab-${t.id}`}
            aria-selected={t.id === tab}
            aria-controls={t.id === tab ? `${id}-panel` : undefined}
            tabIndex={t.id === tab ? 0 : -1}
            onClick={() => setTab(t.id)}
            className={tabClass(t.id === tab)}
          >
            {t.label}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={`${id}-panel`} aria-labelledby={`${id}-tab-${tab}`} className="flex min-h-0 flex-1 flex-col overflow-y-auto">
        {tab === "links" ? (
          <div className="px-[18px] pb-4">
            <BacklinksList path={path} />
          </div>
        ) : (
          <OutlineList headings={headings} current={current} onJump={onJump} />
        )}
      </div>
    </aside>
  );
}
