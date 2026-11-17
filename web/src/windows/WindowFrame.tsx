// SPDX-License-Identifier: AGPL-3.0-or-later
import { X } from "lucide-react";
import { useId, type ReactNode } from "react";
import { useDialog } from "../hooks/useDialog";
import { useCloseWindow } from "./context";
import type { WindowKind } from "./store";

/** Fixed per kind, so that moving inside a window never makes it jump; capped at the viewport less 32 px a side. */
const size: Record<WindowKind, string> = {
  navigated: "lg:w-[min(960px,100vw-64px)] lg:h-[min(720px,100dvh-64px)]",
  console: "lg:w-[min(1100px,100vw-64px)] lg:h-[min(760px,100dvh-64px)]",
  light: "lg:w-[min(672px,100vw-64px)] lg:h-[min(720px,100dvh-64px)]",
};

/**
 * A window over the notes: its title and ✕, Esc through `useDialog`, and a click on the dimmed notes closing a light
 * one only (where nothing typed can be lost). `heading` names the entry shown, for the phone's title; `actions` sit
 * left of ✕.
 */
export default function WindowFrame({
  kind,
  title,
  nav,
  actions,
  children,
}: {
  kind: WindowKind;
  title: string;
  heading?: string;
  nav?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
}) {
  const close = useCloseWindow();
  const dialog = useDialog<HTMLDivElement>(close);
  const titleId = useId();

  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60" onClick={kind === "light" ? (e) => e.target === e.currentTarget && close() : undefined}>
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className={`flex h-full w-full flex-col overflow-hidden border-2 border-line bg-surface lg:rounded-[14px] ${size[kind]}`}
      >
        <div className="flex h-14 flex-none items-center gap-2 border-b border-line bg-surface-2 pl-4">
          <h2 id={titleId} className="min-w-0 flex-1 truncate text-lg font-bold text-text">
            {title}
          </h2>
          {actions}
          <button type="button" aria-label="Close" onClick={close} className="flex size-12 flex-none items-center justify-center text-muted focus-visible:outline-2 focus-visible:outline-accent">
            <X size={20} aria-hidden />
          </button>
        </div>
        <div className="flex min-h-0 flex-1">
          {nav}
          <div className="min-h-0 min-w-0 flex-1 overflow-y-auto">{children}</div>
        </div>
      </div>
    </div>
  );
}
