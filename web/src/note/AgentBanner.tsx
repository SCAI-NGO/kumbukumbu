// SPDX-License-Identifier: AGPL-3.0-or-later
import { useChanges } from "../api/notes";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";

// Before any earlier sign-in there is nothing the user has seen yet: every agent write counts.
const EPOCH = "1970-01-01T00:00:00Z";

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
 * MOBILE_EDITOR §5: the note's last write since the user's previous sign-in was by one of their API keys. "Show"
 * would tint the changed blocks; the API gives no earlier version's text (history has writer, time and version
 * only), so nothing can be marked yet and the banner says so.
 */
export default function AgentBanner({ path }: { path: string }) {
  const since = useSession((s) => s.previousSignInAt) ?? EPOCH;
  const changes = useChanges(since);
  const show = useEditor((s) => s.showAgentHighlights);
  const setShow = useEditor((s) => s.setShowAgentHighlights);
  const change = changes.data?.find((c) => c.path === path);
  if (!change) return null;

  return (
    <section aria-label="Agent changes" className="flex-none border-b border-line bg-surface text-[13px] text-muted">
      <div className="flex min-h-11 items-center gap-2.5 pr-1.5 pl-4">
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
      </div>
      {show && <p className="px-4 pb-2.5 text-[13px]">The changed parts can't be marked yet.</p>}
    </section>
  );
}
