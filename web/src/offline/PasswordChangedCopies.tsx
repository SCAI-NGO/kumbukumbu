// SPDX-License-Identifier: AGPL-3.0-or-later
// After a password change (design §4.2): another device's offline copy still opens with the old password until that
// device next signs in, so the first visit after the change lists those devices, each with "Remove offline copy". The
// copies stay unless removed: removing them would lose changes on the person's own devices that haven't uploaded.
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { DeviceCopies } from "../routes/settings/DeviceCopies";
import { useSession } from "../state/session";
import { listDeviceCopies } from "./slots";

const storageKey = "kumbu.passwordChanged";

/** Set when the password is changed here; the sign-in with the new one follows. */
export function markPasswordChanged(): void {
  try {
    localStorage.setItem(storageKey, "1");
  } catch {
    // Not kept: Settings, Offline still lists the devices.
  }
}

function changed(): boolean {
  try {
    return localStorage.getItem(storageKey) === "1";
  } catch {
    return false;
  }
}

function forget(): void {
  try {
    localStorage.removeItem(storageKey);
  } catch {
    // Nothing kept to forget.
  }
}

export default function PasswordChangedCopies() {
  const user = useSession((s) => s.user);
  const [open, setOpen] = useState(() => changed());
  const devices = useQuery({ queryKey: ["offline-devices"], queryFn: listDeviceCopies, enabled: open && user !== null });
  const others = devices.data?.filter((d) => !d.current).length;
  useEffect(() => {
    // Nothing to say: no other device keeps a copy.
    if (others === 0) {
      forget();
      setOpen(false);
    }
  }, [others]);
  if (!open || user === null || !others) return null;
  return (
    <section aria-label="Offline copies after your password change" className="flex flex-none flex-col gap-2 border-b border-line bg-surface px-4 py-3 text-sm">
      <p className="text-text">
        Your password is changed. These devices keep an offline copy that still opens with your old password until they next
        sign in. Remove a copy you don't trust; one you keep loses nothing it hasn't uploaded.
      </p>
      <DeviceCopies othersOnly />
      <button
        type="button"
        onClick={() => {
          forget();
          setOpen(false);
        }}
        className="min-h-11 self-start text-accent underline"
      >
        Done
      </button>
    </section>
  );
}
