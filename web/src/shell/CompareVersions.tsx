// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo, useState } from "react";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { useNoteVersion } from "../api/notes";
import { useNoteNow } from "../api/activity";
import { FormError } from "../components/form";
import { lineDiff, type DiffLine } from "../note/lineDiff";

export type CompareMode = "before" | "now";

type Props = {
  path: string;
  /** The state the change left; null for a note deleted in it. */
  version: string | null;
  /** The state before the change; null when there was none or it is no longer kept (`beforeLost`). */
  before: string | null;
  /** The note is in the trash now: there is no "now" to compare with. */
  trashed: boolean;
  beforeLost: boolean;
  /** Which comparison shows first (search in earlier versions starts against now). */
  initial?: CompareMode;
  /** What the version is called above its column: "After this change" in the activity, "This version" from search. */
  versionLabel?: string;
};

/**
 * Two versions of a note side by side (stacked on a phone), switchable between "against the version before" and
 * "against now". Removed lines are marked on the left, added ones on the right, in the agent highlights' colours.
 */
export default function CompareVersions({ path, version, before, trashed, beforeLost, initial = "before", versionLabel = "After this change" }: Props) {
  const [mode, setMode] = useState<CompareMode>(initial);
  const againstNow = mode === "now";
  const left = useNoteVersion(path, againstNow ? version : before);
  const right = useNoteVersion(path, againstNow ? null : version);
  const now = useNoteNow(path, againstNow && !trashed);
  const rightText = againstNow ? now.data?.content : right.data?.content;
  const gone = againstNow && (trashed || (now.error instanceof HttpError && now.error.status === 404));

  // Every column either shows lines or says why not: never "Loading" for a version nobody will fetch.
  const unread = (q: { error: unknown }) => (q.error ? "This version couldn't be read" : null);
  const notKept = "This version is no longer kept";
  const leftMissing = againstNow
    ? version === null
      ? notKept
      : unread(left)
    : before === null
      ? beforeLost
        ? "Earlier versions are no longer kept"
        : "Added in this change"
      : unread(left);
  const rightMissing = againstNow ? (gone ? "This note is in the trash now" : unread(now)) : version === null ? notKept : unread(right);
  const diff = useMemo(() => {
    if (rightText === undefined || gone) return null;
    // Added: every line is new. Otherwise there is nothing to compare with, so the text shows as it is.
    if (leftMissing) return leftMissing === "Added in this change" ? lineDiff("", rightText) : lineDiff(rightText, rightText);
    return left.data ? lineDiff(left.data.content, rightText) : null;
  }, [left.data, rightText, leftMissing, gone]);
  // Without a left side, the right one shows its text as it is; without a right side, the left one does.
  const leftOnly = useMemo(() => (rightMissing && left.data ? lineDiff(left.data.content, left.data.content) : null), [rightMissing, left.data]);
  const unchanged = !againstNow && before !== null && before === version;
  const error = [left.error, againstNow ? (gone ? null : now.error) : right.error].find((e) => e && !(e instanceof HttpError && e.status === 404));

  const tabClass = (on: boolean) =>
    `min-h-11 flex-1 rounded-[10px] px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent text-on-accent" : "text-text-body"}`;
  const labels = againstNow ? [versionLabel, "Now"] : ["Before", versionLabel];

  return (
    <>
      <div role="group" aria-label="Compare" className="flex gap-1 rounded-[12px] border border-line p-1">
        <button type="button" aria-pressed={!againstNow} onClick={() => setMode("before")} className={tabClass(!againstNow)}>
          Against the version before
        </button>
        <button type="button" aria-pressed={againstNow} onClick={() => setMode("now")} className={tabClass(againstNow)}>
          Against now
        </button>
      </div>
      {error && <FormError>{errorText(error)}</FormError>}
      {unchanged && <p className="text-sm text-text-body">The text didn't change in this change.</p>}
      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <Column label={labels[0]} message={leftMissing} lines={leftOnly ?? diff?.filter((l) => l.kind !== "added")} />
        <Column label={labels[1]} message={rightMissing} lines={diff?.filter((l) => l.kind !== "removed")} />
      </div>
    </>
  );
}

const lineClass = {
  same: "text-muted",
  removed: "bg-surface-2 text-text before:content-['−_'] before:text-dim",
  added: "bg-[color-mix(in_srgb,var(--accent)_14%,transparent)] text-text before:content-['+_'] before:text-accent-text",
};

function Column({ label, message, lines }: { label: string; message: string | null; lines: DiffLine[] | undefined }) {
  return (
    <section className="flex min-w-0 flex-col gap-1">
      <h3 className="font-mono text-[11px] tracking-[0.08em] text-dim uppercase">{label}</h3>
      {message ? (
        <p className="rounded-[10px] border border-dashed border-line px-3 py-2 text-sm text-muted">{message}</p>
      ) : lines ? (
        <ol aria-label={label} className="overflow-auto rounded-[10px] border border-line bg-bg py-2 font-mono text-[13px] leading-[1.6]">
          {lines.map((line, i) => (
            <li key={i} data-kind={line.kind} className={`min-h-[1.6em] px-3 whitespace-pre-wrap ${lineClass[line.kind]}`}>
              <span>{line.text}</span>
            </li>
          ))}
        </ol>
      ) : (
        <p className="text-sm text-muted">Loading …</p>
      )}
    </section>
  );
}
