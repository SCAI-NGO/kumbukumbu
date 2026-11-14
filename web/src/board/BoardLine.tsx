// SPDX-License-Identifier: AGPL-3.0-or-later
import { sinceText } from "./cardLine";

/** The device's own date: the line is read on the client, and so is its "this year". */
function localToday(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

/** A board card's line in the note's header, beside the flag's and the calendar's: "In Running since 14 Nov". */
export default function BoardLine({ column, since }: { column: string; since: string | null }) {
  return (
    <div role="group" aria-label="On the board" className="flex min-h-12 flex-none items-center border-b border-line bg-surface px-4 text-sm text-text">
      {since ? `In ${column} since ${sinceText(since, localToday())}` : `In ${column}`}
    </div>
  );
}
