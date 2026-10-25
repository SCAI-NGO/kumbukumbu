// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/user-client/src/components/RecoveryKeyReveal.tsx, packages/ui-shared/src/components/ConfirmTyped.tsx)
import { useId, useState } from "react";
import { labelClass, primaryButtonClass, secondaryButtonClass } from "./form";
import { CopyButton, LeaveDialog, useLeaveGuard } from "./shownOnce";

/**
 * The recovery key, shown once. Continue unlocks only after the key was downloaded or copied and the user ticked
 * that it is stored; until it was downloaded or copied, leaving the page (reload, close, back, a link) asks once.
 * The key stays in component state: never in storage, a log or the URL. The downloaded file holds the key and how
 * to keep it, but neither the username nor the instance: a file found on its own is not enough to take the account over.
 */
export function RecoveryKeyReveal({ value, onContinue }: { value: string; onContinue(): void }) {
  const checkId = useId();
  const [saved, setSaved] = useState(false);
  const [stored, setStored] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const blocker = useLeaveGuard(saved);

  const copied = (ok: boolean) => {
    setCopyFailed(!ok);
    if (ok) setSaved(true);
  };

  function download() {
    const text =
      `Kumbu² recovery key\n\n${value}\n\n` +
      "Keep it somewhere safe, such as a password manager, and not on this device.\n" +
      "Together with your username it sets a new password and opens your notes, so whoever has both has your account.\n" +
      "It is shown only once: Kumbu² can't show it again.\n";
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "kumbu-recovery-key.txt";
    link.click();
    // Not in the same tick: Safari can lose the download when the URL goes before it started.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    setSaved(true);
  }

  return (
    <div className="flex flex-col gap-5">
      <p className="text-text-body">
        This is shown only now. If you forget your password, this key is the only way back to your notes: nobody can
        reset it for you, not even an admin.
      </p>

      <div className="flex flex-col gap-1.5">
        <p className={labelClass}>Recovery key</p>
        <pre
          aria-label="Recovery key"
          className="rounded-[12px] border border-line bg-surface p-4 font-mono text-base leading-relaxed tracking-[0.08em] break-all whitespace-pre-wrap text-text select-all"
        >
          {value}
        </pre>
      </div>

      <div className="flex gap-3">
        <button type="button" onClick={download} className={`${secondaryButtonClass} flex-1`}>
          Download
        </button>
        <CopyButton value={value} label={saved && !copyFailed ? "Copy again" : "Copy"} className={`${secondaryButtonClass} flex-1`} onResult={copied} />
      </div>
      {copyFailed && (
        <p aria-live="polite" className="text-sm text-text">
          Copying didn't work. Select the key and copy it by hand, or download it.
        </p>
      )}

      <label htmlFor={checkId} className="flex min-h-11 items-center gap-3 text-text">
        <input
          id={checkId}
          type="checkbox"
          checked={stored}
          onChange={(e) => setStored(e.target.checked)}
          className="size-5 shrink-0 accent-accent"
        />
        I have stored my recovery key somewhere safe, such as a password manager.
      </label>

      <button type="button" disabled={!saved || !stored} onClick={onContinue} className={primaryButtonClass}>
        Continue
      </button>

      {blocker.state === "blocked" && (
        <LeaveDialog
          id={`${checkId}-leave`}
          title="You haven't saved your recovery key"
          text="Without it, a forgotten password means your notes are gone. You won't see this key again."
          onStay={() => blocker.reset?.()}
          onLeave={() => blocker.proceed?.()}
        />
      )}
    </div>
  );
}
