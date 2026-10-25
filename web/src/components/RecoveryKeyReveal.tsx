// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/user-client/src/components/RecoveryKeyReveal.tsx, packages/ui-shared/src/components/ConfirmTyped.tsx)
import { useEffect, useId, useState } from "react";
import { useBlocker } from "react-router-dom";
import { labelClass, primaryButtonClass, secondaryButtonClass } from "./form";

/**
 * The recovery key, shown once. Continue unlocks only after the key was downloaded or copied and the user ticked
 * that it is stored; until it was downloaded or copied, leaving the page (reload, close, back, a link) asks once.
 * The key stays in component state: never in storage, a log or the URL.
 */
export function RecoveryKeyReveal({ value, username, onContinue }: { value: string; username: string; onContinue(): void }) {
  const checkId = useId();
  const [saved, setSaved] = useState(false);
  const [stored, setStored] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const blocker = useBlocker(!saved);

  useEffect(() => {
    if (saved) return;
    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      // Older browsers ask only when returnValue is set.
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [saved]);

  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopyFailed(false);
      setSaved(true);
    } catch {
      // Permission denied or no secure context: the user can still select the key by hand or download it.
      setCopyFailed(true);
    }
  }

  function download() {
    const text = `Kumbu² recovery key for ${username}\n\n${value}\n\nWith it you can set a new password if you forget yours. Nobody else can.\n`;
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = `kumbu-recovery-key-${username}.txt`;
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
        <button type="button" onClick={() => void copy()} className={`${secondaryButtonClass} flex-1`}>
          {saved && !copyFailed ? "Copy again" : "Copy"}
        </button>
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
        <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
          <div
            role="alertdialog"
            aria-modal="true"
            aria-labelledby={`${checkId}-leave`}
            className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
          >
            <h2 id={`${checkId}-leave`} className="text-lg font-bold text-text">
              You haven't saved your recovery key
            </h2>
            <p className="text-text-body">Without it, a forgotten password means your notes are gone. You won't see this key again.</p>
            <div className="flex gap-3">
              <button type="button" onClick={() => blocker.reset()} className={`${primaryButtonClass} flex-1`}>
                Stay
              </button>
              <button type="button" onClick={() => blocker.proceed()} className={`${secondaryButtonClass} flex-1`}>
                Leave without it
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
