// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef } from "react";

const focusable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** The open dialogs, topmost last: only the topmost answers Escape and keeps Tab inside. */
const stack: HTMLElement[] = [];

/**
 * What `aria-modal` promises: focus moves in on open (to `[data-autofocus]`, else the first control), Tab stays
 * inside, Escape calls `onClose` wherever focus is, and focus goes back to the opener on close.
 */
export function useDialog<T extends HTMLElement>(onClose: () => void) {
  const ref = useRef<T>(null);
  const close = useRef(onClose);
  close.current = onClose;

  useEffect(() => {
    const dialog = ref.current!;
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    stack.push(dialog);
    (dialog.querySelector<HTMLElement>("[data-autofocus]") ?? dialog.querySelector<HTMLElement>(focusable) ?? dialog).focus();

    const onKeyDown = (e: KeyboardEvent) => {
      if (stack.at(-1) !== dialog) return;
      if (e.key === "Escape") {
        e.preventDefault();
        close.current();
        return;
      }
      if (e.key !== "Tab") return;
      const controls = [...dialog.querySelectorAll<HTMLElement>(focusable)];
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
      if (opener?.isConnected) opener.focus();
    };
  }, []);

  return ref;
}
