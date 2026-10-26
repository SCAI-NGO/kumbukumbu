// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { BarButton } from "../note/ChangedElsewhere";

const storageKey = "kumbu.installHint";

const matches = (query: string) => {
  try {
    return window.matchMedia?.(query).matches ?? false;
  } catch {
    return false;
  }
};

/** Running as an installed app: Chrome's and Edge's standalone window, or a Home Screen app on iOS. */
export function isStandalone(): boolean {
  return matches("(display-mode: standalone)") || (navigator as Navigator & { standalone?: boolean }).standalone === true;
}

/** A touch screen as the main pointer: a phone or a tablet, where an app icon is what people look for. */
export function isPhone(): boolean {
  return matches("(pointer: coarse)");
}

function dismissed(): boolean {
  try {
    return localStorage.getItem(storageKey) === "dismissed";
  } catch {
    // Storage blocked: the hint would come back on every visit, so it stays away.
    return true;
  }
}

function dismiss(): void {
  try {
    localStorage.setItem(storageKey, "dismissed");
  } catch {
    // Not kept: gone for this visit only.
  }
}

/**
 * Without a service worker Chrome shows no install banner of its own (spec §1): once, on a phone that hasn't
 * installed Kumbu², a short hint leads to the steps in Settings. "Not now" is for good, on this device.
 */
export default function InstallHint() {
  const [shown, setShown] = useState(() => isPhone() && !isStandalone() && !dismissed());
  const navigate = useNavigate();
  if (!shown) return null;

  const close = () => {
    dismiss();
    setShown(false);
  };
  return (
    <section
      aria-label="Install Kumbu² as an app"
      className="flex min-h-11 flex-none flex-wrap items-center gap-x-1 border-b border-line bg-surface pr-1.5 pl-4 text-[13px] text-muted"
    >
      <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />
      <span className="min-w-0 flex-1 py-2 pl-1.5 text-text">Install Kumbu² as an app</span>
      <BarButton
        onClick={() => {
          close();
          navigate("/settings#install");
        }}
      >
        How
      </BarButton>
      <BarButton onClick={close}>Not now</BarButton>
    </section>
  );
}
