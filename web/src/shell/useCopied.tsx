// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from "react";

/**
 * Copies text to the clipboard and says how that went for a few seconds, where the menu was opened: the menu closes
 * on select. The live region stays in the page, so each result is announced, a second "Copied" too; `className`
 * places the line in it. `say` shows any other confirmation there, the same way.
 */
export function useCopied(className = "") {
  // `n` counts the lines: each one shows for its full time, also when it follows another.
  const [state, setState] = useState<{ text: string; n: number } | null>(null);
  useEffect(() => {
    if (!state) return;
    const timer = setTimeout(() => setState(null), 3000);
    return () => clearTimeout(timer);
  }, [state]);
  const say = (text: string) => setState((s) => ({ text, n: (s?.n ?? 0) + 1 }));
  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      say("Copied");
    } catch {
      say("Couldn't copy. Copy it by hand.");
    }
  };
  return {
    copy: (text: string) => void copy(text),
    say,
    status: (
      <div aria-live="polite" className="flex-none">
        {state && (
          <p key={state.n} className={`rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text ${className}`}>
            {state.text}
          </p>
        )}
      </div>
    ),
  };
}
