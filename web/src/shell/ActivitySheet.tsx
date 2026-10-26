// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronDown, ChevronRight } from "lucide-react";
import { useState } from "react";
import { countsText, useActivity, writerText, type ActivityFile, type ActivityRow } from "../api/activity";
import { errorText } from "../api/error-text";
import { folderOf, inVault } from "../api/paths";
import { FormError, secondaryButtonClass } from "../components/form";
import { Sheet, timeText } from "../components/Sheet";
import { ago } from "../note/AgentBanner";
import CompareVersions from "./CompareVersions";

const nameOf = (path: string) => path.slice(path.lastIndexOf("/") + 1);

/** "old.md → plan.md", or with the folders when it moved to another one. */
const renameText = (from: string, to: string) =>
  folderOf(from) === folderOf(to) ? `${nameOf(from)} → ${nameOf(to)}` : `${inVault(from)} → ${inVault(to)}`;

const spanText = (row: ActivityRow) => (row.from === row.to || timeText(row.from) === timeText(row.to) ? timeText(row.to) : `${timeText(row.from)} – ${timeText(row.to)}`);

/**
 * The vault's changesets, newest first: who, when and how many notes of each kind; a row opens into its notes, and a
 * note into a side-by-side comparison inside the same sheet. Says plainly that older changes may be incomplete.
 */
export default function ActivitySheet({ vault, onClose, onOpenTrash }: { vault: string; onClose(): void; onOpenTrash(): void }) {
  const activity = useActivity(vault);
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const [chosen, setChosen] = useState<ActivityFile | null>(null);
  const rows = activity.data?.pages.flatMap((p) => p.rows) ?? [];

  // One dialog for both steps, so the focus stays in it on the way to a note and back.
  if (chosen) {
    return (
      <Sheet title={nameOf(chosen.path)} onClose={onClose} onBack={() => setChosen(null)} backLabel="All changes">
        <p className="truncate font-mono text-[12px] text-dim">{chosen.path}</p>
        {chosen.kind === "deleted" ? (
          <>
            <p className="text-sm text-text-body">Deleted in this change</p>
            <button type="button" onClick={onOpenTrash} className={`${secondaryButtonClass} self-start`}>
              Open the trash
            </button>
          </>
        ) : (
          <CompareVersions path={chosen.path} version={chosen.version} before={chosen.before} trashed={chosen.trashed} beforeLost={chosen.beforeLost} />
        )}
      </Sheet>
    );
  }

  const key = (row: ActivityRow, i: number) => `${i}-${row.id ?? ""}-${row.to}`;
  const toggle = (k: string) =>
    setOpen((s) => {
      const next = new Set(s);
      if (!next.delete(k)) next.add(k);
      return next;
    });

  return (
    <Sheet title="Activity" onClose={onClose}>
      {activity.isPending && <p className="text-sm text-muted">Loading the changes …</p>}
      {activity.isError && <FormError>{errorText(activity.error)}</FormError>}
      {activity.data && rows.length === 0 && <p className="text-sm text-muted">No changes yet.</p>}
      {rows.length > 0 && (
        <ul aria-label="Changes" className="flex flex-col gap-1.5">
          {rows.map((row, i) => {
            const k = key(row, i);
            const expanded = open.has(k);
            const Chevron = expanded ? ChevronDown : ChevronRight;
            return (
              <li key={k} className="rounded-[12px] border border-line bg-surface-2">
                <button
                  type="button"
                  aria-expanded={expanded}
                  onClick={() => toggle(k)}
                  className="flex min-h-12 w-full items-start gap-2 px-3.5 py-2 text-left focus-visible:outline-2 focus-visible:outline-accent"
                >
                  <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                    <span className="flex w-full items-baseline gap-2">
                      <span className="min-w-0 flex-1 truncate font-medium text-text">{writerText(row)}</span>
                      <span className="flex-none text-[12px] text-dim">{ago(row.to)}</span>
                    </span>
                    <span className="text-sm text-muted">{spanText(row)}</span>
                    <span className="text-sm text-text-body">{countsText(row.files)}</span>
                  </span>
                  <Chevron size={18} aria-hidden className="mt-1 flex-none text-dim" />
                </button>
                {expanded && (
                  <ul aria-label="Files" className="flex flex-col border-t border-line py-1">
                    {row.files.map((f, j) => (
                      <li key={`${j}-${f.path}`}>
                        <button
                          type="button"
                          onClick={() => setChosen(f)}
                          className="flex min-h-11 w-full items-baseline gap-2 px-3.5 py-1.5 text-left focus-visible:outline-2 focus-visible:outline-accent"
                        >
                          <span className="min-w-0 flex-1 truncate text-text">{f.kind === "renamed" && f.from ? renameText(f.from, f.path) : inVault(f.path)}</span>
                          <span className="flex-none font-mono text-[11px] text-dim">
                            {f.kind}
                            {f.trashed && f.kind !== "deleted" ? " · in the trash" : ""}
                          </span>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {activity.hasNextPage && (
        <button type="button" disabled={activity.isFetchingNextPage} onClick={() => void activity.fetchNextPage()} className={`${secondaryButtonClass} self-center`}>
          Load more
        </button>
      )}
      <p className="text-sm text-muted">Older changes may be incomplete: Kumbu² keeps the last 10 versions of each note.</p>
    </Sheet>
  );
}
