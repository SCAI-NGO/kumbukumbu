// SPDX-License-Identifier: AGPL-3.0-or-later
import { TriangleAlert } from "lucide-react";
import { useMemo, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { deviceOf } from "../api/activity";
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
 * The note's last write since the user's previous visit, when one of their API keys made it. The first
 * visit ever has no earlier one to compare with, so it shows no banner and asks nothing.
 */
export function useAgentChange(path: string): Change | undefined {
  const since = useSession((s) => s.previousSignInAt);
  return useChanges(since).data?.find((c) => c.path === path);
}

/** Who wrote, without the tool: "liz (edit)" is "liz" (as Writers.Identity on the server). */
export const writerOf = (label: string) => label.split(" (")[0];

/**
 * The version the user last saw, roughly: the newest one before the agents' writes since the previous visit,
 * i.e. written before it, by the user, or by one of their devices (Obsidian). Null when there is none: an agent
 * wrote the note from the start. A device back after a long pause (`change`) is not taken for the user: its writes
 * since then are what the banner asks about.
 */
export function lastSeen(history: HistoryEntry[], since: string, username: string, change?: Change): string | null {
  const from = Date.parse(since);
  const back = change?.writerKind === "device" && change.quietSince ? { writer: change.writer, after: Date.parse(change.quietSince) } : null;
  const seenDevice = (v: HistoryEntry) => v.writerKind === "device" && !(back && writerOf(v.writer) === back.writer && Date.parse(v.writtenAt) > back.after);
  return history.find((v) => !v.current && (Date.parse(v.writtenAt) <= from || writerOf(v.writer) === username || seenDevice(v)))?.version ?? null;
}

/** The writer a highlight names: a device as "Obsidian (Pixel 9)", not by its key's whole name. */
const highlightWriter = (change: Change) => (change.writerKind === "device" ? `Obsidian (${deviceOf(change.writer)})` : change.writer);

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
  const base = on && history.data && username ? lastSeen(history.data, since!, username, change) : null;
  const earlier = useNoteVersion(path, base);

  return useMemo(() => {
    if (!on || !history.data) return null;
    if (base === null) return { lines: "all", writer: highlightWriter(change!) };
    if (!earlier.data) return null;
    return { lines: changedLines(earlier.data.content, content!), writer: highlightWriter(change!) };
  }, [on, history.data, base, earlier.data, content, change]);
}

/** How long a device wrote nothing, roughly: "16 days", "3 weeks", "2 months", "1 year". Always more than 14 days. */
export function pauseText(from: string, to: string): string {
  const days = Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000);
  const [n, unit] = days < 21 ? [days, "day"] : days < 61 ? [Math.round(days / 7), "week"] : days < 365 ? [Math.round(days / 30), "month"] : [Math.round(days / 365), "year"];
  return `${n} ${unit}${n === 1 ? "" : "s"}`;
}

/**
 * "Agent key liz edited 3 minutes ago", with Show / Hide for the highlights of what it changed. When that write isn't
 * on screen yet, the note view puts its Refresh here (`actions`, and a line under the text in `children`) instead of
 * a second bar about the same write. A device's write is here only when the device wrote again after a long pause
 * (#43): a device key doesn't expire, so a stolen one would otherwise write as the user unnoticed. That banner
 * stands out, names the device, and leads to Access, where it is revoked.
 */
export default function AgentBanner({ change, actions, children }: { change: Change | undefined; actions?: ReactNode; children?: ReactNode }) {
  const show = useEditor((s) => s.showAgentHighlights);
  const setShow = useEditor((s) => s.setShowAgentHighlights);
  if (!change) return null;
  const resumed = change.writerKind === "device" && change.quietSince && change.resumedAt ? { from: change.quietSince, to: change.resumedAt } : null;

  return (
    <section
      aria-label={resumed ? "Device changes" : "Agent changes"}
      className={`flex min-h-11 flex-none items-center gap-2.5 border-b border-line pr-1.5 pl-4 text-[13px] ${resumed ? "border-l-4 border-l-accent bg-surface-2 text-text" : "bg-surface text-muted"}`}
    >
      {resumed ? <TriangleAlert aria-hidden className="size-4 flex-none text-accent-text" /> : <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />}
      <span className="min-w-0 flex-1 py-2">
        {resumed ? (
          <>
            Obsidian on <strong className="font-semibold">{deviceOf(change.writer)}</strong> wrote here {ago(change.lastWriteAt)}, for the first time in{" "}
            {pauseText(resumed.from, resumed.to)}. If that wasn't you, revoke the device in{" "}
            <Link to="/keys" className="font-medium text-accent-text underline underline-offset-2">
              Access
            </Link>
            .
          </>
        ) : (
          <>
            Agent key <code className="font-mono text-text">{change.writer}</code> edited {ago(change.lastWriteAt)}
          </>
        )}
        {children}
      </span>
      {actions}
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
