// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef } from "react";

const focusable = 'a[href], button:not([disabled]), input:not([disabled]):not([tabindex="-1"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** The open dialogs, topmost last: only the topmost answers Escape and keeps Tab inside. */
const stack: HTMLElement[] = [];

/** The topmost open dialog, if any. */
export const topDialog = (): HTMLElement | null => stack.at(-1) ?? null;

/** The dialog's controls within reach: not those under an `inert` part of it (the content under a window's open pane). */
const controlsOf = (dialog: HTMLElement) => [...dialog.querySelectorAll<HTMLElement>(focusable)].filter((el) => !el.closest("[inert]"));

/**
 * What `aria-modal` promises: focus moves in on open (to `[data-autofocus]`, else the first control), Tab stays
 * inside, Escape calls `onClose` wherever focus is, and focus goes back to the opener on close. An opener that only
 * held the focus for a moment (`data-focus-return="<id>"`, the dock's keyboard stand-in) hands it to that element;
 * without one (a deep link) or with one gone meanwhile, `fallback` names where the focus goes instead.
 */
export function useDialog<T extends HTMLElement>(onClose: () => void, fallback?: () => HTMLElement | null) {
  const ref = useRef<T>(null);
  const close = useRef(onClose);
  close.current = onClose;
  const instead = useRef(fallback);
  instead.current = fallback;

  useEffect(() => {
    const dialog = ref.current!;
    const active = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const opener = active?.dataset.focusReturn ? document.getElementById(active.dataset.focusReturn) : active;
    stack.push(dialog);
    (dialog.querySelector<HTMLElement>("[data-autofocus]") ?? controlsOf(dialog)[0] ?? dialog).focus();

    const onKeyDown = (e: KeyboardEvent) => {
      if (stack.at(-1) !== dialog) return;
      if (e.key === "Escape") {
        e.preventDefault();
        close.current();
        return;
      }
      if (e.key !== "Tab") return;
      const controls = controlsOf(dialog);
      if (controls.length === 0) return;
      const at = controls.indexOf(document.activeElement as HTMLElement);
      const outside = !dialog.contains(document.activeElement);
      // Wrap at either end; a focused heading (not a control) inside the dialog tabs on normally.
      if (e.shiftKey && (at <= 0 || outside)) {
        e.preventDefault();
        controls.at(-1)!.focus();
      } else if (!e.shiftKey && (at === controls.length - 1 || outside)) {
        e.preventDefault();
        controls[0].focus();
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      stack.splice(stack.indexOf(dialog), 1);
      if (opener?.isConnected && opener !== document.body) opener.focus();
      else instead.current?.()?.focus();
    };
  }, []);

  return ref;
}
