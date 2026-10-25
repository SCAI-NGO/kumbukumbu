// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/user-client/src/components/RecoveryKeyReveal.tsx)
import { useEffect } from "react";
import { useBlocker } from "react-router-dom";
import { useDialog } from "../hooks/useDialog";
import { primaryButtonClass, secondaryButtonClass } from "./form";

/**
 * For a secret shown once: until it was saved (downloaded or copied), reloading or closing the page asks the
 * browser's question, and leaving inside the app is blocked so the caller can ask with {@link LeaveDialog}.
 */
export function useLeaveGuard(saved: boolean) {
  const blocker = useBlocker(!saved);

  useEffect(() => {
    if (saved) return;
    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      // Older browsers ask only when returnValue is set.
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [saved]);

  return blocker;
}

/** "Leave without it?": Escape stays, as the safe answer. */
export function LeaveDialog({
  id,
  title,
  text,
  leaveLabel = "Leave without it",
  onStay,
  onLeave,
}: {
  id: string;
  title: string;
  text: string;
  leaveLabel?: string;
  onStay(): void;
  onLeave(): void;
}) {
  const dialog = useDialog<HTMLDivElement>(onStay);
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={id}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={id} className="text-lg font-bold text-text">
          {title}
        </h2>
        <p className="text-text-body">{text}</p>
        <div className="flex gap-3">
          <button type="button" onClick={onStay} className={`${primaryButtonClass} flex-1`}>
            Stay
          </button>
          <button type="button" onClick={onLeave} className={`${secondaryButtonClass} flex-1`}>
            {leaveLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
