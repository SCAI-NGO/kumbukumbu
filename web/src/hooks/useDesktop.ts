// SPDX-License-Identifier: AGPL-3.0-or-later
import { mediaQueryHook } from "./mediaQuery";

/**
 * Whether the window is desktop-wide, Tailwind's `lg` (64rem, 1024 px): from here on the tree sits beside the note
 * instead of in a drawer over it. Without `matchMedia` it counts as a phone, the layout that works anywhere.
 */
export const useDesktop = mediaQueryHook("(min-width: 64rem)");
