// SPDX-License-Identifier: AGPL-3.0-or-later
import { mediaQueryHook } from "./mediaQuery";

/** Whether the graph column fits beside the note and the side panel: from 120rem (1920 px). */
export const useGraphWide = mediaQueryHook("(min-width: 120rem)");
/** The same in the split view, with two panes beside each other: from 180rem (2880 px). */
export const useGraphWideSplit = mediaQueryHook("(min-width: 180rem)");
