// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect } from "react";
import { topDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";
import { useWindows } from "../windows/store";

/** A Mac's keyboard: Cmd is the shortcuts' key there, and Ctrl+P stays the editor's "line up". */
export const isMac = () => /Mac|iPhone|iPad|iPod/.test(navigator.platform);

/**
 * Ctrl (Cmd on a Mac) with the letter `key`, and no other modifier. By its place (`code`) only where the key types no
 * Latin letter (Cyrillic, Greek): on Dvorak the place of P types L, and Ctrl+L stays the browser's.
 */
export const withMod = (e: Pick<KeyboardEvent, "key" | "code" | "altKey" | "shiftKey" | "metaKey" | "ctrlKey">, key: string) =>
  (/^[a-z]$/i.test(e.key) ? e.key.toLowerCase() === key : e.code === `Key${key.toUpperCase()}`) && !e.altKey && !e.shiftKey && (isMac() ? e.metaKey && !e.ctrlKey : e.ctrlKey && !e.metaKey);

/**
 * Ctrl+P (Cmd+P on a Mac) opens the palette, and closes it again: in the capture phase, before the editor or the
 * browser's Print sees it. Over the notes or a window only, not over a dialog, nor over a window locked on a secret.
 */
export function usePaletteShortcut() {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (!withMod(e, "p")) return;
      const { overlay, setOverlay } = useEditor.getState();
      const top = topDialog();
      if (overlay !== "palette" && top && (top.dataset.frame === undefined || useWindows.getState().lock !== null)) return;
      e.preventDefault();
      setOverlay(overlay === "palette" ? "none" : "palette");
    };
    document.addEventListener("keydown", onKeyDown, true);
    return () => document.removeEventListener("keydown", onKeyDown, true);
  }, []);
}
