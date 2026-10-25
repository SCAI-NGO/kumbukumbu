// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo } from "react";
import { useChanges, useHistory, useNoteVersion, type HistoryEntry } from "../api/notes";
import type { components } from "../api/schema";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { changedLines } from "./lineDiff";
import type { Highlight } from "./MarkdownView";

export type Change = components["schemas"]["ChangeView"];

const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["day", 86_400],
  ["hour", 3_600],
  ["minute", 60],
];
const relative = new Intl.RelativeTimeFormat("en-GB", { numeric: "auto" });

/** "3 minutes ago", "yesterday", "just now". */
export function ago(iso: string, now = Date.now()) {
  const seconds = (Date.parse(iso) - now) / 1000;
  const [unit, size] = units.find(([, s]) => Math.abs(seconds) >= s) ?? [];
  return unit ? relative.format(Math.round(seconds / size!), unit) : "just now";
}

/**
 * The note's last write since the user's previous sign-in, when one of their API keys made it. The first
 * sign-in ever has no earlier visit to compare with, so it shows no banner and asks nothing.
 */
export function useAgentChange(path: string): Change | undefined {
  const since = useSession((s) => s.previousSignInAt);
  return useChanges(since).data?.find((c) => c.path === path);
}

/** Who wrote, without the tool: "liz (mv)" is "liz" (as Writers.Identity on the server). */
const identity = (writer: string) => writer.split(" (")[0];

/**
 * The version the user last saw, roughly: the newest one before the agents' writes since the previous sign-in,
 * i.e. written before it or by the user. Null when there is none: an agent wrote the note from the start.
 */
export function lastSeen(history: HistoryEntry[], since: string, username: string): string | null {
  const from = Date.parse(since);
  return history.find((v) => !v.current && (Date.parse(v.writtenAt) <= from || identity(v.writer) === username))?.version ?? null;
}

/**
 * MOBILE_EDITOR §5 "Show": the source lines of `content` that changed since the version the user last saw (a
 * line diff against it, read with `?version=`), or all of them. Asked for only while the highlights are on.
 */
export function useAgentHighlight(path: string, change: Change | undefined, content: string | undefined): Highlight | null {
  const show = useEditor((s) => s.showAgentHighlights);
  const since = useSession((s) => s.previousSignInAt);
  const username = useSession((s) => s.user?.username);
  const on = show && !!change && since !== null && content !== undefined;
  const history = useHistory(path, on);
  const base = on && history.data && username ? lastSeen(history.data, since!, username) : null;
  const earlier = useNoteVersion(path, base);

  return useMemo(() => {
    if (!on || !history.data) return null;
    if (base === null) return { lines: "all", writer: change!.writer };
    if (!earlier.data) return null;
    return { lines: changedLines(earlier.data.content, content!), writer: change!.writer };
  }, [on, history.data, base, earlier.data, content, change]);
}

/** "Agent key liz edited 3 minutes ago", with Show / Hide for the highlights of what it changed. */
export default function AgentBanner({ change }: { change: Change | undefined }) {
  const show = useEditor((s) => s.showAgentHighlights);
  const setShow = useEditor((s) => s.setShowAgentHighlights);
  if (!change) return null;

  return (
    <section aria-label="Agent changes" className="flex min-h-11 flex-none items-center gap-2.5 border-b border-line bg-surface pr-1.5 pl-4 text-[13px] text-muted">
      <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />
      <span className="min-w-0 flex-1">
        Agent key <code className="font-mono text-text">{change.writer}</code> edited {ago(change.lastWriteAt)}
      </span>
      <button
        type="button"
        aria-pressed={show}
        onClick={() => setShow(!show)}
        className="h-11 flex-none rounded-[8px] px-2.5 font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        {show ? "Hide" : "Show"}
      </button>
    </section>
  );
}
