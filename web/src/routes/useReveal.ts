// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef, useState } from "react";

/**
 * A secret made on a tab of the access page, held in component state only until its reveal is done, and the page
 * told when the reveal starts and ends. Leaving in the middle of a reveal ends it too, so the page shows its tabs
 * again on the way back.
 */
export function useReveal<T>(onRevealChange?: (revealing: boolean) => void) {
  const [shown, setShown] = useState<T | null>(null);
  const report = useRef(onRevealChange);
  report.current = onRevealChange;

  useEffect(() => () => report.current?.(false), []);

  const reveal = (value: T | null) => {
    setShown(value);
    report.current?.(value !== null);
  };
  return [shown, reveal] as const;
}
