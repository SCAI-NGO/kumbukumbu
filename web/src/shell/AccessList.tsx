// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { errorText } from "../api/error-text";
import { roles, useMembership, useMembershipChange, useVaultAccess, type MemberAccess, type Role } from "../api/sharing";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { timeText } from "../components/Sheet";
import { useDialog } from "../hooks/useDialog";
import { maintainSentence } from "../routes/rights";
import ShareDialog from "./ShareDialog";

const roleLabels: Record<Role, string> = { read: "Read", write: "Read and write", maintain: "Maintain" };
const kindLabels: Record<string, string> = { key: "API key", connection: "App" };

/**
 * Who opened Bob's link and waits for the owner: the one place she sees a member's username (collab design §4.2).
 * The display name comes quoted from the server, as free text someone typed.
 */
function Redeemed({ vault, member, onGrant, onDecline, pending }: { vault: string; member: MemberAccess; onGrant(): void; onDecline(): void; pending: boolean }) {
  const membership = useMembership(vault, member.id, true);
  const redeemer = membership.data?.redeemer;
  return (
    <div className="flex flex-col gap-2">
      {membership.isError && <FormError>{errorText(membership.error)}</FormError>}
      {redeemer && (
        <p className="text-text-body">
          {redeemer.name} (username <span className="font-mono">{redeemer.username}</span>) accepted <em>{member.label}</em>'s invitation — grant access?
        </p>
      )}
      <div className="flex gap-2">
        <button type="button" onClick={onGrant} disabled={!redeemer || pending} className={secondaryButtonClass}>
          Grant access
        </button>
        <button type="button" onClick={onDecline} disabled={pending} className={secondaryButtonClass}>
          Decline
        </button>
      </div>
    </div>
  );
}

/**
 * Revoking: at once, for the member and every key and app she connected. The re-encryption that would also shut out an
 * old backup is offered off by default (collab concept §10.5); until a shared vault can be re-encrypted (a later
 * step: the server refuses it while the vault is shared), the choice is shown but can't be made, and the way to it
 * once nobody else shares the vault is named.
 */
function RevokeDialog({ vault, label, pending, error, onRevoke, onClose }: { vault: string; label: string; pending: boolean; error: string | null; onRevoke(): void; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={(e) => {
          e.preventDefault();
          onRevoke();
        }}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Revoke {label}'s access to {vault}?
        </h2>
        <p className="text-text-body">
          {label}, and every key and app {label} connected, lose access to {vault} at once. What they read before stays with them.
        </p>
        <label className="flex items-start gap-3 text-muted">
          <input type="checkbox" checked={false} disabled aria-describedby={`${id}-why ${id}-later`} className="mt-0.5 size-5 shrink-0" />
          <span>Also re-encrypt this vault</span>
        </label>
        <p id={`${id}-why`} className="text-sm text-text-body">
          Only needed if {label} could get hold of an old backup of this server. Everyone's app connections and keys for this vault stop working and need to
          be set up again.
        </p>
        <p id={`${id}-later`} className="text-sm text-muted">
          Not possible here yet: a vault that is shared can't be re-encrypted. Once nobody else shares {vault}, Settings can re-encrypt it.
        </p>
        {error && <FormError>{error}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={pending} className={`${primaryButtonClass} flex-1`}>
            Revoke
          </button>
        </div>
      </form>
    </div>
  );
}

/** One person: her role, what she connected, and what the owner can do now. */
function Member({ vault, member }: { vault: string; member: MemberAccess }) {
  const id = useId();
  const change = useMembershipChange(vault);
  const [revoking, setRevoking] = useState(false);
  const role = member.role as Role;
  const invited = member.status === "pending" && !member.redeemedAt;
  const waiting = member.status === "pending" && !!member.redeemedAt;
  return (
    <li aria-labelledby={`${id}-label`} className="flex flex-col gap-2 rounded-[12px] border border-line bg-surface-2 p-3">
      <p id={`${id}-label`} className="font-medium break-words text-text">
        {member.label}
      </p>
      {invited && (
        <>
          <p className="text-sm text-muted">
            Invited as {roleLabels[role] ?? member.role}
            {member.expiresAt ? `; the link works until ${timeText(member.expiresAt)}` : ""}.
          </p>
          <button type="button" onClick={() => change.mutate({ kind: "withdraw", id: member.id })} disabled={change.isPending} className={`${secondaryButtonClass} self-start`}>
            Withdraw
          </button>
        </>
      )}
      {waiting && (
        <Redeemed
          vault={vault}
          member={member}
          pending={change.isPending}
          onGrant={() => change.mutate({ kind: "confirm", id: member.id })}
          onDecline={() => change.mutate({ kind: "decline", id: member.id })}
        />
      )}
      {member.status === "active" && (
        <>
          <div className="flex flex-col gap-1">
            <label htmlFor={`${id}-role`} className={labelClass}>
              Role
            </label>
            <select
              id={`${id}-role`}
              value={role}
              disabled={change.isPending}
              onChange={(e) => change.mutate({ kind: "role", id: member.id, role: e.target.value as Role })}
              className={`${inputClass} ${role === "maintain" ? "text-warning" : ""}`}
            >
              {roles.map((r) => (
                <option key={r} value={r}>
                  {roleLabels[r]}
                </option>
              ))}
            </select>
            {role === "maintain" && <p className="text-sm text-warning">{maintainSentence}</p>}
            {member.acceptedAt && <p className="text-sm text-muted">A member since {timeText(member.acceptedAt)}.</p>}
          </div>
          {member.credentials.length > 0 ? (
            <ul aria-label={`Keys and apps of ${member.label}`} className="flex flex-col gap-1 text-sm text-text-body">
              {member.credentials.map((c, i) => (
                <li key={`${c.kind}-${c.name}-${i}`} className="break-words">
                  {c.name} · {kindLabels[c.kind] ?? c.kind} · {c.lastUsedAt ? `last used here ${timeText(c.lastUsedAt)}` : "not used here yet"}
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-muted">No keys or apps of theirs for this vault.</p>
          )}
          <button type="button" onClick={() => setRevoking(true)} className={`${secondaryButtonClass} self-start`}>
            Revoke…
          </button>
        </>
      )}
      {change.isError && !revoking && <FormError>{errorText(change.error)}</FormError>}
      {revoking && (
        <RevokeDialog
          vault={vault}
          label={member.label}
          pending={change.isPending}
          error={change.isError ? errorText(change.error) : null}
          onRevoke={() => change.mutate({ kind: "revoke", id: member.id }, { onSuccess: () => setRevoking(false) })}
          onClose={() => {
            change.reset();
            setRevoking(false);
          }}
        />
      )}
    </li>
  );
}

/** The owner's view of who her vault is shared with, and the way to invite someone (collab design §8.6, §10.1). */
export default function AccessList({ vault, onClose }: { vault: string; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const access = useVaultAccess(vault);
  const [inviting, setInviting] = useState(false);
  const members = access.data?.members ?? [];
  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
          Who has access to {vault}
        </h2>
        {access.isPending && <p className="text-muted">Looking up who has access…</p>}
        {access.isError && <FormError>{errorText(access.error)}</FormError>}
        {access.data && members.length === 0 && <p className="text-text-body">{vault} isn't shared with anyone.</p>}
        {members.length > 0 && (
          <ul aria-label="Members" className="flex flex-col gap-2">
            {members.map((m) => (
              <Member key={m.id} vault={vault} member={m} />
            ))}
          </ul>
        )}
        <button type="button" onClick={() => setInviting(true)} className={secondaryButtonClass}>
          Invite someone…
        </button>
        <button type="button" onClick={onClose} className={primaryButtonClass}>
          Done
        </button>
      </div>
      {inviting && <ShareDialog vault={vault} onClose={() => setInviting(false)} />}
    </div>
  );
}
