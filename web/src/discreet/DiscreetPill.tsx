// SPDX-License-Identifier: AGPL-3.0-or-later
import { Eye, X } from "lucide-react";
import { useEffect, useState } from "react";
import { useHideAgain } from "./hideAgain";
import { useDiscreet } from "./state";

/** Whole minutes left until `until`, rounded up: "9 min" while anything of the ninth is left. */
export const minutesLeft = (until: string, now = Date.now()) => Math.max(1, Math.ceil((Date.parse(until) - now) / 60_000));

/** The minutes left, again whenever the shown number changes; none for Never. */
function useMinutesLeft(until: string | null) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    if (!until) return;
    const left = Date.parse(until) - Date.now();
    // Past the end the number stays at 1 until the mode ends.
    if (left <= 0) return;
    // The next change of the rounded-up number, just after the minute turns.
    const timer = setTimeout(() => setNow(Date.now()), Math.max(0, left % 60_000) + 50);
    return () => clearTimeout(timer);
  }, [until, now]);
  return until ? minutesLeft(until, now) : null;
}

/** Whether the discreet vaults are shown now: the frame and the pill come with it. */
export const useRevealed = () => useDiscreet((s) => s.revealed);

/**
 * The amber frame (spec §3.7): drawn over what it frames, inside the nearest positioned box, so that bars with their own
 * background don't cover it; it takes no clicks.
 */
export function DiscreetFrame() {
  return <div aria-hidden data-discreet-frame className="pointer-events-none absolute inset-0 z-30 inset-ring-2 inset-ring-discreet-line" />;
}

/**
 * "Discreet · 9 min · ✕" while the discreet vaults are shown (spec §3.7), where the vault menu sits: the time left, or
 * none for Never, and ✕ hides them again at once.
 */
export default function DiscreetPill() {
  const until = useDiscreet((s) => s.until);
  const minutes = useMinutesLeft(until);
  const hide = useHideAgain();
  return (
    <div role="group" aria-label="Discreet vaults shown" className="flex min-h-9 w-fit max-w-full items-center gap-1.5 rounded-full border border-discreet-line bg-discreet-soft pr-0.5 pl-3 text-sm font-medium text-discreet">
      <Eye size={16} aria-hidden className="flex-none" />
      <span className="truncate">
        Discreet
        {minutes !== null && (
          <span className="font-mono text-[13px]">
            <span aria-hidden> · </span>
            {minutes} min
          </span>
        )}
      </span>
      <span aria-hidden>·</span>
      <button
        type="button"
        onClick={() => void hide()}
        aria-label="Hide discreet vaults"
        title="Hide discreet vaults"
        // 32 px drawn, 44 px to tap: the area reaches past the circle on every side.
        className="relative flex size-8 flex-none items-center justify-center rounded-full after:absolute after:-inset-1.5 hover:bg-discreet/15 focus-visible:outline-2 focus-visible:outline-discreet"
      >
        <X size={16} aria-hidden />
      </button>
    </div>
  );
}
