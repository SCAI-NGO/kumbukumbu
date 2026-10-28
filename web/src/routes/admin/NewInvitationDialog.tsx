// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/admin-client/src/routes/invitations/create-modal.tsx)
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { PasswordField } from "../../components/PasswordField";
import { useDialog } from "../../hooks/useDialog";

const roles = [
  { value: "user", label: "User" },
  { value: "admin", label: "Admin" },
] as const;
const expiries = [
  { value: 1, label: "1 day" },
  { value: 7, label: "7 days" },
  { value: 30, label: "30 days" },
] as const;

/** Segmented radio choice, the same pattern as the key rights. */
function Choice<T extends string | number>({ legend, name, options, value, onChange }: {
  legend: string;
  name: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange(value: T): void;
}) {
  return (
    <fieldset className="flex flex-col gap-1.5">
      <legend className={`${labelClass} mb-1.5`}>{legend}</legend>
      <div className="flex rounded-[10px] border border-line bg-surface-2 p-0.5">
        {options.map((option) => (
          <label
            key={option.value}
            className="flex min-h-11 flex-1 cursor-pointer items-center justify-center rounded-[8px] px-2 text-center text-sm text-text-body has-checked:bg-accent has-checked:text-on-accent has-focus-visible:outline-2 has-focus-visible:outline-accent"
          >
            <input type="radio" name={name} checked={value === option.value} onChange={() => onChange(option.value)} className="sr-only" />
            {option.label}
          </label>
        ))}
      </div>
    </fieldset>
  );
}

/** Asks for the role, how long the invitation is valid and an optional note; for an admin, the admin's own password too. */
export default function NewInvitationDialog({ onClose, onCreated }: { onClose(): void; onCreated(link: string): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const [role, setRole] = useState<"user" | "admin">("user");
  const [days, setDays] = useState<1 | 7 | 30>(7);
  const [note, setNote] = useState("");
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");

  const create = useMutation({
    mutationFn: () =>
      call(
        api.POST("/api/admin/invitations", {
          body: { role, expiresInDays: days, ...(note.trim() ? { note: note.trim() } : {}), ...(role === "admin" ? { password } : {}) },
        }),
      ),
    // The result carries the link: it goes to the reveal's state, not the client-wide mutation cache.
    gcTime: 0,
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["admin", "invitations"] });
      onCreated(created.link);
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    create.mutate();
  };

  return (
    <div className="fixed inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={submit}
        className="k-sheet flex max-h-dvh w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
          New invitation
        </h2>
        <Choice<"user" | "admin"> legend="Role" name={`${id}-role`} options={roles} value={role} onChange={setRole} />
        {role === "admin" && (
          <div className="flex flex-col gap-1.5">
            <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />
            <p className="text-sm text-muted">An admin can manage every account here: your password confirms the invitation.</p>
          </div>
        )}
        <Choice<1 | 7 | 30> legend="Valid for" name={`${id}-days`} options={expiries} value={days} onChange={setDays} />
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-note`} className={labelClass}>
            Note (optional)
          </label>
          <input id={`${id}-note`} value={note} onChange={(e) => setNote(e.target.value)} autoComplete="off" maxLength={200} className={inputClass} />
          <p className="text-sm text-muted">Only you and other admins see it, to remember who the invitation is for.</p>
        </div>
        {create.isError && <FormError>{errorText(create.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={create.isPending || (role === "admin" && !password)} className={primaryButtonClass}>
            Create invitation
          </button>
        </div>
      </form>
    </div>
  );
}
