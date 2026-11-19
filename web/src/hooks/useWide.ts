// SPDX-License-Identifier: AGPL-3.0-or-later
import { mediaQueryHook } from "./mediaQuery";

/**
 * Whether the window is wide, Tailwind's `xl` (80rem, 1280 px): from here on the side panel sits beside the note.
 * Below, and without `matchMedia`, the note is as on a narrower desktop.
 */
export const useWide = mediaQueryHook("(min-width: 80rem)");
