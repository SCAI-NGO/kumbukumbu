// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from "react";

/**
 * How many pixels of the layout viewport's bottom the on-screen keyboard covers: the visual viewport shrinks
 * (and on iOS scrolls) when it opens, the layout viewport does not. 0 where the browser resizes the page itself.
 */
export function useVisualViewport(): number {
  const [covered, setCovered] = useState(0);

  useEffect(() => {
    const viewport = window.visualViewport;
    if (!viewport) return;
    const update = () => setCovered(Math.max(0, Math.round(window.innerHeight - viewport.height - viewport.offsetTop)));
    update();
    viewport.addEventListener("resize", update);
    viewport.addEventListener("scroll", update);
    return () => {
      viewport.removeEventListener("resize", update);
      viewport.removeEventListener("scroll", update);
    };
  }, []);

  return covered;
}
