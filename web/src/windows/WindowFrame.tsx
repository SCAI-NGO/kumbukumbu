// SPDX-License-Identifier: AGPL-3.0-or-later
import { X } from "lucide-react";
import { useId, type ReactNode } from "react";
import { useDesktop } from "../hooks/useDesktop";
import { useDialog } from "../hooks/useDialog";
import { useCloseWindow } from "./context";
import { useWindows, type WindowKind } from "./store";

/** Fixed per kind, so that moving inside a window never makes it jump; capped at the viewport less 32 px a side. */
const size: Record<WindowKind, string> = {
  navigated: "lg:w-[min(960px,100vw-64px)] lg:h-[min(720px,100dvh-64px)]",
  console: "lg:w-[min(1100px,100vw-64px)] lg:h-[min(760px,100dvh-64px)]",
  light: "lg:w-[min(672px,100vw-64px)] lg:h-[min(720px,100dvh-64px)]",
};

/** Robust windows hold work that can be lost, so they are loud: a firm border, a strong shadow, a dark backdrop. Light ones stay quiet. */
const frame: Record<"robust" | "light", { window: string; bar: string; backdrop: string }> = {
  robust: { window: "border-2 shadow-2xl", bar: "bg-surface-2", backdrop: "bg-black/60" },
  light: { window: "border shadow-lg", bar: "bg-surface", backdrop: "bg-black/35" },
};

/** Below `lg` a window is a sheet: full width, top at 6 % of the height (at least 32 px) under the safe area, flush with the bottom. */
const sheet = "absolute inset-x-0 bottom-0 top-[calc(max(32px,6dvh)+env(safe-area-inset-top))] rounded-t-[16px] border-b-0 pb-[env(safe-area-inset-bottom)] motion-safe:animate-[k-slide-up_200ms_ease-out]";

/**
 * A window over the notes: its title and ✕, Esc through `useDialog`, and a click on the dimmed notes closing a light
 * one only (where nothing typed can be lost). `heading` names the entry shown, for the phone's title; `actions` sit
 * left of ✕.
 */
export default function WindowFrame({
  kind,
  title,
  heading,
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
  // Closed with nothing that opened it left (a deep link, a menu gone), the focus goes to the shell's top bar, not the page.
  const dialog = useDialog<HTMLDivElement>(close, () => document.querySelector<HTMLElement>("[data-window-return]"));
  const titleId = useId();
  const desktop = useDesktop();
  const navOpen = useWindows((s) => s.navOpen);
  const light = kind === "light";
  const look = frame[light ? "light" : "robust"];

  return (
    <div className="fixed inset-0 z-[55] flex items-center justify-center">
      <div aria-hidden className={`absolute inset-0 ${look.backdrop}`} onClick={light ? close : undefined} />
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        data-frame={light ? "light" : "robust"}
        data-sheet={desktop ? undefined : ""}
        className={`flex flex-col overflow-hidden border-line bg-surface ${look.window} ${desktop ? `relative rounded-[14px] ${size[kind]}` : sheet}`}
      >
        <div className={`flex h-14 flex-none items-center gap-2 border-b border-line pl-4 ${look.bar}`}>
          <div className="flex min-w-0 flex-1 items-baseline gap-2">
            <h2 id={titleId} className="min-w-0 truncate text-lg font-bold text-text">
              {title}
            </h2>
            {!desktop && heading && <span className="min-w-0 truncate text-lg text-muted">· {heading}</span>}
          </div>
          {actions && <div className="mr-2 flex flex-none items-center">{actions}</div>}
          {/* The ring drawn inside the target, rounded: the window's corner would clip one outside it. */}
          <button type="button" aria-label="Close" onClick={close} className="flex size-12 flex-none items-center justify-center rounded-[10px] text-muted focus-visible:outline-2 focus-visible:-outline-offset-4 focus-visible:outline-accent">
            <X size={20} aria-hidden />
          </button>
        </div>
        <div className="relative flex min-h-0 flex-1">
          {nav}
          <div inert={navOpen} className={`min-h-0 min-w-0 flex-1 overflow-y-auto ${nav && !desktop ? "pl-10" : ""}`}>{children}</div>
        </div>
      </div>
    </div>
  );
}
