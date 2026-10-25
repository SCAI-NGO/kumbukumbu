// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/admin-client/src/routes/invitations/reveal-screen.tsx)
import { useId, useState } from "react";
import { labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { CopyButton, LeaveDialog, useLeaveGuard } from "../components/shownOnce";

/** The line that adds Kumbu² to Claude Code with this key. */
export const claudeCodeLine = (key: string) =>
  `claude mcp add --transport http kumbu ${window.location.origin}/mcp --header "Authorization: Bearer ${key}"`;

/**
 * A new API key, shown once, with copy buttons for the key and for the Claude Code line. Until one of them was
 * copied, leaving the page or closing the key asks once. The key stays in component state only.
 */
export default function KeyReveal({ name, value, onDone }: { name: string; value: string; onDone(): void }) {
  const id = useId();
  const [saved, setSaved] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const [closing, setClosing] = useState(false);
  const blocker = useLeaveGuard(saved);
  const line = claudeCodeLine(value);

  const copied = (ok: boolean) => {
    setCopyFailed(!ok);
    if (ok) setSaved(true);
  };

  const leave = {
    id: `${id}-leave`,
    title: "You haven't saved your API key",
    text: "You won't see this key again. Without it, you'll need to make a new one.",
  };

  return (
    <section aria-labelledby={`${id}-title`} className="flex flex-col gap-5">
      <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
        Your new key “{name}”
      </h2>
      <p className="text-text-body">This is shown only now. Copy the key, or the line for Claude Code, and keep it somewhere safe.</p>

      <div className="flex flex-col gap-1.5">
        <p className={labelClass}>API key</p>
        <pre
          aria-label="API key"
          className="rounded-[12px] border border-line bg-surface p-4 font-mono text-base leading-relaxed break-all whitespace-pre-wrap text-text select-all"
        >
          {value}
        </pre>
        <CopyButton value={value} label="Copy key" className={secondaryButtonClass} onResult={copied} />
      </div>

      <div className="flex flex-col gap-1.5">
        <p className={labelClass}>Claude Code</p>
        <pre
          aria-label="Claude Code command"
          className="rounded-[12px] border border-line bg-surface p-4 font-mono text-sm leading-relaxed break-all whitespace-pre-wrap text-text-body select-all"
        >
          {line}
        </pre>
        <CopyButton value={line} label="Copy command" className={secondaryButtonClass} onResult={copied} />
      </div>

      {copyFailed && (
        <p aria-live="polite" className="text-sm text-text">
          Copying didn't work. Select the key and copy it by hand.
        </p>
      )}

      <button type="button" onClick={() => (saved ? onDone() : setClosing(true))} className={primaryButtonClass}>
        Done
      </button>

      {blocker.state === "blocked" && <LeaveDialog {...leave} onStay={() => blocker.reset?.()} onLeave={() => blocker.proceed?.()} />}
      {closing && <LeaveDialog {...leave} onStay={() => setClosing(false)} onLeave={onDone} />}
    </section>
  );
}
