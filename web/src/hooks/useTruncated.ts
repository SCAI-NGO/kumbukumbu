// SPDX-License-Identifier: AGPL-3.0-or-later
import { useRef, useState } from "react";

/**
 * For a button whose label (`ref`, a `truncate` element) may be cut off: `title` is the full text, but only once the
 * label is measured as cut off, on hover or keyboard focus. A `title` on every entry would make screen readers say
 * every name twice.
 */
export function useTruncated<L extends HTMLElement>(full: string) {
  const ref = useRef<L>(null);
  const [cut, setCut] = useState(false);
  const measure = () => setCut(!!ref.current && ref.current.scrollWidth > ref.current.clientWidth);
  return { ref, title: cut ? full : undefined, onPointerEnter: measure, onFocus: measure };
}
