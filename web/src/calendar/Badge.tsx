// SPDX-License-Identifier: AGPL-3.0-or-later
import { AlertTriangle } from "lucide-react";

/** What the badge says to a screen reader: "3 on", "3 on, some overdue". */
export const badgeLabel = (count: number, overdue: boolean) => (overdue ? `${count} on, ${count === 1 ? "overdue" : "some overdue"}` : `${count} on`);

/**
 * The count of What's on (spec §7.7): nothing at zero; the scheme's warning colour when anything is overdue, a calm
 * one otherwise, and never the colour alone: its label, and a small warning icon when overdue. `id` lets a control
 * whose name is its own (the phone's tree button) take it as its description.
 */
export default function Badge({ count, overdue, id, className = "" }: { count: number; overdue: boolean; id?: string; className?: string }) {
  if (count <= 0) return null;
  return (
    <span
      id={id}
      className={`inline-flex h-5 min-w-5 flex-none items-center justify-center gap-0.5 rounded-full px-1.5 text-xs font-semibold tabular-nums ${overdue ? "bg-warning text-bg" : "border border-line bg-surface-2 text-text"} ${className}`}
    >
      {overdue && <AlertTriangle aria-hidden size={11} strokeWidth={2.4} className="flex-none" />}
      <span aria-hidden>{count > 99 ? "99+" : count}</span>
      <span className="sr-only">{badgeLabel(count, overdue)}</span>
    </span>
  );
}
