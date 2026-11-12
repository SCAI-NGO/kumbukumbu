// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from "react";
import { schemes } from "./schemes";

// An absent or unknown scheme counts as light.
const isDark = () => schemes.find((s) => s.id === document.documentElement.dataset.scheme)?.light === false;

/** Whether the scheme on <html> is a dark one; `applyScheme` fires no event, so its attribute is watched. */
export function useDarkScheme(): boolean {
  const [dark, setDark] = useState(isDark);
  useEffect(() => {
    const observer = new MutationObserver(() => setDark(isDark()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-scheme"] });
    setDark(isDark());
    return () => observer.disconnect();
  }, []);
  return dark;
}
