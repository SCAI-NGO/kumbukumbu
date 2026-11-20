// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef, useState } from "react";
import type { MenuPoint } from "../components/PointMenu";
import { topDialog } from "../hooks/useDialog";
import { useEditor } from "../state/editor";

/** Shift+F10 or the context-menu key, every platform's key for a context menu. */
const isMenuKey = (e: KeyboardEvent) => e.key === "ContextMenu" || (e.key === "F10" && e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey);

/** Where the dock's own note menu opens: above its ⋯, ending at the button's right edge. */
function atDockButton(): MenuPoint | null {
  const button = document.querySelector<HTMLElement>('nav[aria-label="Vault actions"] [aria-label="Note actions"]');
  if (!button) return null;
  const box = button.getBoundingClientRect();
  // Ending at its right edge whatever the room to the right: the dock's own menu does, and a window wide enough to open
  // rightwards would otherwise put the list beside the button.
  return { left: box.right, top: box.top - 4, bottom: box.bottom, align: "end" };
}

/**
 * Keeps the browser's own context menu away after the key: Windows raises it on the key's keyup, which a prevented
 * keydown doesn't stop. Only for a moment (until just after that keyup, else 500 ms), so a right click stays the
 * browser's. Answers how to stop at once.
 */
function swallowContextMenu(key: string) {
  const swallow = (e: Event) => e.preventDefault();
  const onKeyUp = (e: KeyboardEvent) => {
    if (e.key !== key) return;
    clearTimeout(timer);
    timer = setTimeout(disarm, 0);
  };
  const disarm = () => {
    clearTimeout(timer);
    document.removeEventListener("contextmenu", swallow, true);
    document.removeEventListener("keyup", onKeyUp, true);
  };
  let timer = setTimeout(disarm, 500);
  document.addEventListener("contextmenu", swallow, true);
  document.addEventListener("keyup", onKeyUp, true);
  return disarm;
}

/**
 * The open note's menu from the keyboard (Shift+F10, the context-menu key): in Write mode at the caret, in Read mode
 * where the dock's ⋯ opens it. In the capture phase, before the editor or the browser's own context menu; not over a
 * dialog, the palette or another menu, and not without a note. Answers where the menu is open, and how it closes:
 * with the focus back where it was, in the editor at the same caret.
 */
export function useNoteMenuKey(path: string | null): { at: MenuPoint; close(): void } | null {
  const [open, setOpen] = useState<{ at: MenuPoint; opener: HTMLElement | null } | null>(null);
  const shown = useRef(path);
  shown.current = path;
  const isOpen = useRef(false);
  isOpen.current = open !== null;

  useEffect(() => {
    // One at a time, and none left behind by the shell.
    let disarm = () => {};
    const swallow = (key: string) => {
      disarm();
      disarm = swallowContextMenu(key);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (!isMenuKey(e) || e.isComposing || !shown.current) return;
      // The key held down, or pressed again, over our own menu: not the browser's on top of it.
      if (isOpen.current) {
        e.preventDefault();
        swallow(e.key);
        return;
      }
      const { overlay, mode, caretAt } = useEditor.getState();
      if (topDialog() || overlay !== "none" || document.querySelector('[role="menu"]')) return;
      const at = mode === "write" ? (caretAt?.() ?? null) : atDockButton();
      if (!at) return;
      e.preventDefault();
      swallow(e.key);
      setOpen({ at, opener: document.activeElement instanceof HTMLElement ? document.activeElement : null });
    };
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      disarm();
    };
  }, []);

  // Another note, or the other mode: the menu was for what was shown.
  const mode = useEditor((s) => s.mode);
  useEffect(() => setOpen(null), [path, mode]);

  return (
    open && {
      at: open.at,
      close: () => {
        setOpen(null);
        open.opener?.focus();
      },
    }
  );
}
