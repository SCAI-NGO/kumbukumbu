// SPDX-License-Identifier: AGPL-3.0-or-later
import { ChevronLeft } from "lucide-react";
import { Link } from "react-router-dom";
import type { CameFrom } from "./backLink";

/** ‹ Calendar or ‹ What's on in a note's header: back to the view the note was opened from, as it was left. */
export default function BackLink({ from }: { from: CameFrom }) {
  return (
    <nav aria-label="Back" className="flex flex-none items-center border-b border-line bg-surface px-2 text-sm">
      <Link
        to={from.to}
        aria-label={from.label === "Calendar" ? "Back to the calendar" : "Back to What's on"}
        className="flex min-h-12 items-center gap-1 rounded-[10px] px-2 font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent"
      >
        <ChevronLeft aria-hidden size={18} />
        {from.label}
      </Link>
    </nav>
  );
}
