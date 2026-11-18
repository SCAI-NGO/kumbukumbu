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
  return { left: box.right, top: box.top - 4, bottom: box.bottom };
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

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (!isMenuKey(e) || e.isComposing || !shown.current) return;
      const { overlay, mode, caretAt } = useEditor.getState();
      if (topDialog() || overlay !== "none" || document.querySelector('[role="menu"]')) return;
      const at = mode === "write" ? (caretAt?.() ?? null) : atDockButton();
      if (!at) return;
      e.preventDefault();
      setOpen({ at, opener: document.activeElement instanceof HTMLElement ? document.activeElement : null });
    };
    document.addEventListener("keydown", onKeyDown, true);
    return () => document.removeEventListener("keydown", onKeyDown, true);
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
