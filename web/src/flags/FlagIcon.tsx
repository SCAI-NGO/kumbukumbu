// SPDX-License-Identifier: AGPL-3.0-or-later
import { Check, ExclamationMark, Flag } from "lucide-react";
import { isFlag, type FlagName } from "./api";

/** What the menu calls each flag. */
export const flagLabel: Record<FlagName, string> = {
  red: "Red",
  green: "Green",
  blue: "Blue",
  purple: "Purple",
  yellow: "Yellow",
  white: "White",
  done: "Done",
  important: "Important",
};

/** What the icon says of itself: "Red flag", "Done", "Important". */
export const flagName = (flag: FlagName) => (flag === "done" || flag === "important" ? flagLabel[flag] : `${flagLabel[flag]} flag`);

// Literal class names, so that Tailwind finds them.
const colour: Record<FlagName, string> = {
  red: "text-flag-red",
  green: "text-flag-green",
  blue: "text-flag-blue",
  purple: "text-flag-purple",
  yellow: "text-flag-yellow",
  white: "text-text",
  done: "text-flag-green",
  important: "text-warning",
};

/**
 * A flag as its icon (spec §2.3): a flag filled in its colour, the white one outlined, a green tick for Done and an
 * exclamation mark for Important. It names itself, so that it is never told apart by its colour alone; `decorative`
 * where a name beside it says it already (the menu's "Red"). Nothing for a tag that is no flag.
 */
export default function FlagIcon({ flag, size = 16, decorative = false, className = "" }: { flag: string | null | undefined; size?: number; decorative?: boolean; className?: string }) {
  if (!isFlag(flag)) return null;
  const name = flagName(flag);
  const label = decorative ? { "aria-hidden": true } : { role: "img", "aria-label": name };
  const classes = `flex-none ${colour[flag]} ${className}`;
  if (flag === "done") return <Check size={size} strokeWidth={2.6} {...label} className={classes}>{!decorative && <title>{name}</title>}</Check>;
  if (flag === "important") return <ExclamationMark size={size} strokeWidth={2.8} {...label} className={classes}>{!decorative && <title>{name}</title>}</ExclamationMark>;
  return (
    <Flag size={size} strokeWidth={flag === "white" ? 2 : 1.8} fill={flag === "white" ? "none" : "currentColor"} {...label} className={classes}>
      {!decorative && <title>{name}</title>}
    </Flag>
  );
}
