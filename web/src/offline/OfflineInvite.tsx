// SPDX-License-Identifier: AGPL-3.0-or-later
// The one invitation to keep the notes on this device (design §3a, "Turning it on"): only where the device plainly is
// the person's own (an installed app, or "Keep me signed in" at the last sign-in), once per device: it counts as asked
// only once answered, so one that appeared and wasn't answered comes again at the next start.
import { MirrorCopies } from "eidolon-core/store/mirror";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { BarButton } from "../note/ChangedElsewhere";
import { useSession } from "../state/session";
import { isStandalone } from "../update/InstallHint";
import { remembered, storePorts } from "./turnOn";

const storageKey = "kumbu.offlineInvite";

function asked(): boolean {
  try {
    return localStorage.getItem(storageKey) === "asked";
  } catch {
    // Storage blocked: it would come back on every visit, so it stays away.
    return true;
  }
}

function markAsked(): void {
  try {
    localStorage.setItem(storageKey, "asked");
  } catch {
    // Not kept: gone for this visit only.
  }
}

export default function OfflineInvite() {
  const user = useSession((s) => s.user);
  const [shown, setShown] = useState(false);
  const navigate = useNavigate();
  useEffect(() => {
    if (user === null || asked() || !(isStandalone() || remembered())) return;
    let live = true;
    void storePorts()
      .then((ports) => new MirrorCopies(ports).forUser(user.id))
      .then((copy) => {
        if (!live || copy !== null) return;
        setShown(true);
      })
      .catch(() => {});
    return () => {
      live = false;
    };
  }, [user]);
  if (!shown) return null;
  return (
    <section
      aria-label="Keep your notes on this device"
      className="flex min-h-11 flex-none flex-wrap items-center gap-x-1 border-b border-line bg-surface pr-1.5 pl-4 text-[13px] text-muted"
    >
      <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />
      <span className="min-w-0 flex-1 py-2 pl-1.5 text-text">Keep your notes on this device for when you're offline?</span>
      <BarButton
        onClick={() => {
          markAsked();
          setShown(false);
          navigate("/settings/offline");
        }}
      >
        Set up
      </BarButton>
      <BarButton
        onClick={() => {
          markAsked();
          setShown(false);
        }}
      >
        Not now
      </BarButton>
    </section>
  );
}
