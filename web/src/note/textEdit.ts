// SPDX-License-Identifier: AGPL-3.0-or-later

/** A replacement of `start`–`end` by `text`, and the selection to leave afterwards. */
export type Edit = { start: number; end: number; text: string; selStart: number; selEnd: number };

/**
 * Applies an edit as typing would, so the browser's undo can take it back: `insertText` where the browser still
 * offers it, else `setRangeText` (no undo, but the same text). Both leave the textarea focused.
 */
export function apply(area: HTMLTextAreaElement, edit: Edit, onChange: (value: string) => void) {
  if (document.activeElement !== area) area.focus({ preventScroll: true });
  area.setSelectionRange(edit.start, edit.end);
  // execCommand is deprecated, yet it is the only way into the undo stack; it fires `input`, so React sees it.
  const typed = typeof document.execCommand === "function" && document.execCommand("insertText", false, edit.text);
  if (!typed) {
    area.setRangeText(edit.text, edit.start, edit.end);
    onChange(area.value);
  }
  area.setSelectionRange(edit.selStart, edit.selEnd);
}
