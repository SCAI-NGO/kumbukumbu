// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { pdfsNotYet, usePasswordWindowOpen, usePublish } from "../api/publications";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { CopyButton } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";

/**
 * The owner publishes the note (publish spec §3.5, §3.11): her password, as for an invitation, unless the password
 * window is open. The link stays in this component's state; the bar above the note copies it again later.
 */
export default function PublishDialog({ path, onClose }: { path: string; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const passwordWindow = usePasswordWindowOpen();
  // Read through the closure of the submit, and cleared once used.
  const [password, setPassword] = useState("");
  const [published, setPublished] = useState<{ link: string; plain: string[] } | null>(null);
  const [copyFailed, setCopyFailed] = useState(false);
  const publish = usePublish(path);
  const asks = passwordWindow.known && !passwordWindow.open;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!passwordWindow.known || publish.isPending || (asks && password === "")) return;
    publish.mutate(asks ? { password } : {}, {
      onSuccess: (view) => {
        setPassword("");
        setPublished({ link: view.link, plain: view.plain });
        publish.reset();
      },
    });
  };

  const names = published?.plain.filter((p) => p !== pdfsNotYet) ?? [];

  return (
    <div className="absolute inset-0 z-50 flex items-end justify-center bg-black/55 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
          Publish this note
        </h2>
        {published ? (
          <>
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-link`} className={labelClass}>
                Link
              </label>
              <input id={`${id}-link`} readOnly value={published.link} onFocus={(e) => e.target.select()} className={`${inputClass} font-mono text-sm`} />
              <CopyButton value={published.link} label="Copy link" className={secondaryButtonClass} onResult={(ok) => setCopyFailed(!ok)} />
            </div>
            {copyFailed && (
              <p aria-live="polite" className="text-sm text-text">
                Copying didn't work. Select the link and copy it by hand.
              </p>
            )}
            {names.length > 0 && <p className="text-sm text-text-body">Not copied, plain text instead: {names.join(", ")}.</p>}
            {published.plain.includes(pdfsNotYet) && <p className="text-sm text-text-body">{pdfsNotYet}.</p>}
            <button type="button" onClick={onClose} className={primaryButtonClass}>
              Done
            </button>
          </>
        ) : (
          <form onSubmit={submit} className="flex flex-col gap-4" aria-busy={publish.isPending}>
            <p className="text-text-body">Anyone with the link can read a copy of this note as it is now. Links to other notes become plain text.</p>
            {asks && <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />}
            {publish.isError && <FormError>{errorText(publish.error)}</FormError>}
            <div className="flex gap-2">
              <button type="button" onClick={onClose} className={secondaryButtonClass}>
                Cancel
              </button>
              <button type="submit" disabled={!passwordWindow.known || publish.isPending || (asks && password === "")} className={primaryButtonClass}>
                Publish
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
