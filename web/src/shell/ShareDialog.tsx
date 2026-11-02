// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { roles, type Role } from "../api/sharing";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass, TextField } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { CopyButton } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";
import { maintainClass, segmentClass } from "../routes/NewKeyDialog";
import { maintainSentence } from "../routes/rights";

const roleLabels: Record<Role, string> = { read: "Read", write: "Read and write", maintain: "Maintain" };

/** How long the link lasts: the server takes 1 to 30 days. */
const expiries = [1, 7, 14, 30];

/**
 * The owner invites someone to her vault (collab design §4.1, §10.1): her name for the person, the role, how long the
 * link lasts, whether she confirms whoever opens it, and her password. The link comes back once and lives in this
 * component's state only: closed, it is gone, here and in the caches; a new link takes a new invitation.
 */
export default function ShareDialog({ vault, onClose }: { vault: string; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const [label, setLabel] = useState("");
  // Write is enough for most; maintain is chosen deliberately (collab concept §7).
  const [role, setRole] = useState<Role>("write");
  const [days, setDays] = useState(7);
  const [confirm, setConfirm] = useState(false);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  // The password is asked in a step of its own: Bitwarden filled the text field beside it with the username in spite
  // of data-bwignore, which silently changes who the invitation is for.
  const [asking, setAsking] = useState(false);
  const [link, setLink] = useState<string | null>(null);
  const [copyFailed, setCopyFailed] = useState(false);

  const create = useMutation({
    mutationFn: () => call(api.POST("/api/vaults/{vault}/invitations", { params: { path: { vault } }, body: { password, label: label.trim(), role, expiresInDays: days, confirm } })),
    // The answer carries the link: it goes to this component's state, not the client-wide mutation cache.
    gcTime: 0,
    onSuccess: (created) => {
      setPassword("");
      setLink(created.link);
      create.reset();
      void queryClient.invalidateQueries({ queryKey: ["vault-access", vault] });
      void queryClient.invalidateQueries({ queryKey: ["vaults"] });
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!asking) {
      if (label.trim() !== "") setAsking(true);
    } else if (password !== "" && !create.isPending) create.mutate();
  };

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
          Share {vault}
        </h2>
        {link ? (
          <>
            <p className="text-text-body">
              This link is shown only now. Send it through a safe channel, such as a password manager's secure send or a one-time secret link: whoever opens
              it first {confirm ? "asks you for access" : "gets access"}, and it works once.
            </p>
            <div className="flex flex-col gap-1.5">
              <p className={labelClass}>Invitation link</p>
              <pre
                aria-label="Invitation link"
                className="rounded-[12px] border border-line bg-surface-2 p-4 font-mono text-sm leading-relaxed break-all whitespace-pre-wrap text-text select-all"
              >
                {link}
              </pre>
              <CopyButton value={link} label="Copy link" className={secondaryButtonClass} onResult={(ok) => setCopyFailed(!ok)} />
            </div>
            {copyFailed && (
              <p aria-live="polite" className="text-sm text-text">
                Copying didn't work. Select the link and copy it by hand.
              </p>
            )}
            <button type="button" onClick={onClose} className={primaryButtonClass}>
              Done
            </button>
          </>
        ) : asking ? (
          <form onSubmit={submit} className="flex flex-col gap-4" aria-busy={create.isPending}>
            <p className="text-text-body">
              A link for <em>{label.trim()}</em> to {roleLabels[role].toLowerCase()}. Your password confirms it.
            </p>
            <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />
            {create.isError && <FormError>{errorText(create.error)}</FormError>}
            <div className="flex gap-2">
              <button
                type="button"
                onClick={() => {
                  setPassword("");
                  create.reset();
                  setAsking(false);
                }}
                className={secondaryButtonClass}
              >
                Back
              </button>
              <button type="submit" disabled={password === "" || create.isPending} className={primaryButtonClass}>
                Create link
              </button>
            </div>
          </form>
        ) : (
          <form onSubmit={submit} className="flex flex-col gap-4">
            <TextField label="Who it is for" value={label} onChange={setLabel} autoComplete="off" ignoreManagers />
            <p className="-mt-2 text-sm text-muted">Your name for them, shown only to you. Whoever opens the link gets in under it.</p>

            <fieldset className="flex min-w-0 flex-col gap-1">
              <legend className={`${labelClass} mb-1`}>Role</legend>
              <div className="flex rounded-[10px] border border-line bg-surface-2 p-0.5">
                {roles.map((r) => (
                  <label key={r} className={r === "maintain" ? maintainClass : segmentClass}>
                    <input type="radio" name={`${id}-role`} value={r} checked={role === r} onChange={() => setRole(r)} className="sr-only" />
                    {roleLabels[r]}
                  </label>
                ))}
              </div>
              {role === "maintain" && <p className="text-sm text-warning">{maintainSentence}</p>}
            </fieldset>

            <fieldset className="flex min-w-0 flex-col gap-1">
              <legend className={`${labelClass} mb-1`}>The link expires after</legend>
              <div className="flex rounded-[10px] border border-line bg-surface-2 p-0.5">
                {expiries.map((d) => (
                  <label key={d} className={segmentClass}>
                    <input type="radio" name={`${id}-expiry`} value={d} checked={days === d} onChange={() => setDays(d)} className="sr-only" />
                    {d === 1 ? "1 day" : `${d} days`}
                  </label>
                ))}
              </div>
            </fieldset>

            <label className="flex min-h-11 cursor-pointer items-start gap-3 text-text">
              <input type="checkbox" checked={confirm} onChange={(e) => setConfirm(e.target.checked)} className="mt-0.5 size-5 shrink-0 accent-accent" />
              <span>
                Ask me to confirm before access is granted
                <span className="block text-sm text-muted">You see who opened the link, with their username, and decide then.</span>
              </span>
            </label>

            <p className="text-sm text-text-body">
              Members with <em>write</em> can change and delete notes. Deleted notes stay in the trash for 30 days, and earlier versions in the history, as far
              as its limits go.
            </p>
            <p className="text-sm text-text-body">New members see everything in the vault, also what was written before they joined.</p>

            <div className="flex gap-2">
              <button type="button" onClick={onClose} className={secondaryButtonClass}>
                Cancel
              </button>
              <button type="submit" disabled={label.trim() === ""} className={primaryButtonClass}>
                Continue
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
