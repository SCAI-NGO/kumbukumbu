// SPDX-License-Identifier: AGPL-3.0-or-later

// The star colours: the hotter the star, the more recently the note was written, as on a blackbody ramp from
// blue-white through white, yellow and orange to red. Marks, not text, so each stop reads at 3:1 (WCAG 1.4.11) on
// the grounds of every scheme of its kind (theme/contrast.test.ts). The light ramp is the dark one, darkened.
export const darkRamp: readonly string[] = ["#9cc4ff", "#f5f5f5", "#ffd84a", "#ff9a3c", "#ff6a5a"];
export const lightRamp: readonly string[] = ["#1f5fc4", "#5c6670", "#826300", "#a64a00", "#b82c1c"];

/** Notes with no known time of writing, ghosts and attachments; a mark like the stops, so held to 3:1 the same way. */
export const neutral = { dark: "#959daa", light: "#626a77" };

/** An hour old or younger is the hottest stop; half a year or older the coldest. */
export const AGE_MIN_MS = 3_600_000;
export const AGE_MAX_MS = 180 * 86_400_000;
