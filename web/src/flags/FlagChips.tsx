// SPDX-License-Identifier: AGPL-3.0-or-later
import type { ReactNode } from "react";
import type { FlagFilter } from "../shell/treeFilter";
import { FLAGS, type FlagName } from "./api";
import FlagIcon, { flagLabel } from "./FlagIcon";

/** A 48 px target around a chip that looks smaller. */
const chipTarget = "flex min-h-12 min-w-12 items-center rounded-[8px] focus-visible:outline-2 focus-visible:outline-accent";

const chipClass = (pressed: boolean) =>
  `flex h-9 items-center gap-1.5 rounded-full border px-3 text-[13px] ${pressed ? "border-accent font-semibold text-text ring-1 ring-accent" : "border-line text-text-body"}`;

/** A chip that is pressed or not. */
export function Chip({ pressed, onClick, children }: { pressed: boolean; onClick(): void; children: ReactNode }) {
  return (
    <button type="button" aria-pressed={pressed} onClick={onClick} className={chipTarget}>
      <span className={chipClass(pressed)}>{children}</span>
    </button>
  );
}

/**
 * Any flag and the eight flags (spec §3.1), one at a time: pressed again, a chip lets go. Each says its name: the
 * flags' colours alone don't tell them apart. Named apart from the note's own flag in its header. By default one row
 * that scrolls sideways, as nine chips would wrap to three rows on a phone. With `shown` (the tree's Find by name, UI
 * round spec §2.1), only those flags and the one pressed, wrapping: none in use and none pressed, no chips at all.
 */
export default function FlagChips({ flag, onFlag, shown }: { flag: FlagFilter | null; onFlag(flag: FlagFilter | null): void; shown?: readonly FlagName[] }) {
  const flags = shown ? FLAGS.filter((f) => shown.includes(f) || flag === f) : FLAGS;
  if (shown && flags.length === 0 && flag === null) return null;
  return (
    <div role="group" aria-label="Find by flag" className={`flex min-w-0 gap-x-1 p-0.5 ${shown ? "flex-wrap" : "flex-nowrap overflow-x-auto [&>button]:flex-none"}`}>
      <Chip pressed={flag === "any"} onClick={() => onFlag(flag === "any" ? null : "any")}>
        Any flag
      </Chip>
      {flags.map((f) => (
        <Chip key={f} pressed={flag === f} onClick={() => onFlag(flag === f ? null : f)}>
          <FlagIcon flag={f} size={14} decorative />
          {flagLabel[f]}
        </Chip>
      ))}
    </div>
  );
}
