// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { useTimeout } from "../note/ChangedElsewhere";

/**
 * Copies text to the clipboard and says how that went for a few seconds, where the menu was opened: the menu closes
 * on select. `className` places the line.
 */
export function useCopied(className = "") {
  const [state, setState] = useState<"copied" | "failed" | null>(null);
  useTimeout(state !== null, 3000, () => setState(null));
  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setState("copied");
    } catch {
      setState("failed");
    }
  };
  return {
    copy: (text: string) => void copy(text),
    status: state && (
      <p role="status" className={`rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text ${className}`}>
        {state === "copied" ? "Copied" : "Couldn't copy. Copy it by hand."}
      </p>
    ),
  };
}
