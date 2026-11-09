// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from "react";

/**
 * Copies text to the clipboard and says how that went for a few seconds, where the menu was opened: the menu closes
 * on select. The live region stays in the page, so each result is announced, a second "Copied" too; `className`
 * places the line in it.
 */
export function useCopied(className = "") {
  // `n` counts the copies: each one shows for its full time, also when it follows another.
  const [state, setState] = useState<{ result: "copied" | "failed"; n: number } | null>(null);
  useEffect(() => {
    if (!state) return;
    const timer = setTimeout(() => setState(null), 3000);
    return () => clearTimeout(timer);
  }, [state]);
  const copy = async (text: string) => {
    let result: "copied" | "failed" = "copied";
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      result = "failed";
    }
    setState((s) => ({ result, n: (s?.n ?? 0) + 1 }));
  };
  return {
    copy: (text: string) => void copy(text),
    status: (
      <div aria-live="polite" className="flex-none">
        {state && (
          <p key={state.n} className={`rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text ${className}`}>
            {state.result === "copied" ? "Copied" : "Couldn't copy. Copy it by hand."}
          </p>
        )}
      </div>
    ),
  };
}
