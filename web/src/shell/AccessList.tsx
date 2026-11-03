// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import {
  roles,
  useMembership,
  useMembershipChange,
  useRaiseToMaintain,
  useRekeyVault,
  useVaultAccess,
  withdrawnSentence,
  type Departure,
  type MemberAccess,
  type Role,
} from "../api/sharing";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PasswordField } from "../components/PasswordField";
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

/** The concept's sentence for the offer of a re-encryption (collab concept §10.5). */
function ReencryptWhy({ id, who }: { id: string; who: string }) {
  return (
    <p id={id} className="text-sm text-text-body">
      Only needed if {who} could get hold of an old backup of this server. It doesn't help if {who} can also change this server's database. Everyone's app
      connections and keys for this vault stop working and need to be set up again.
    </p>
  );
}

/**
 * Revoking: at once, for the member and every key and app she connected. The re-encryption that would also shut out an
 * old backup is offered, off by default (collab concept §10.5); ticked, it asks for the password, which is checked
 * before anything is revoked, and runs once the access is gone, so the new key never reaches the person revoked.
 */
function RevokeDialog({ vault, member, onDone, onClose }: { vault: string; member: MemberAccess; onDone(message: string): void; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const queryClient = useQueryClient();
  const [reencrypt, setReencrypt] = useState(false);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const [revoked, setRevoked] = useState(false);
  const label = member.label;
  const revoke = useMutation({
    mutationFn: async () => {
      if (reencrypt) await call(api.POST("/api/session/confirm", { body: { password } }));
      if (!revoked) {
        await call(api.POST("/api/vaults/{vault}/memberships/{id}/revoke", { params: { path: { vault, id: member.id } } }));
        setRevoked(true);
      }
      return reencrypt ? await call(api.POST("/api/vaults/{vault}/rekey", { params: { path: { vault } }, body: { password } })) : null;
    },
    onSuccess: (view) => {
      setPassword("");
      onDone((reencrypt ? `${label}'s access is revoked, and ${vault} is re-encrypted.` : `${label}'s access is revoked.`) + withdrawnSentence(view));
    },
    onSettled: () => Promise.all(["vault-access", "vaults", "keys", "connections"].map((key) => queryClient.invalidateQueries({ queryKey: [key] }))),
  });
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-busy={revoke.isPending}
        onSubmit={(e) => {
          e.preventDefault();
          if (!reencrypt || password) revoke.mutate();
        }}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Revoke {label}'s access to {vault}?
        </h2>
        <p className="text-text-body">
          {label}, and every key and app {label} connected, lose access to {vault} at once. What they read before stays with them.
        </p>
        <label className="flex items-start gap-3 text-text">
          <input
            type="checkbox"
            checked={reencrypt}
            disabled={revoke.isPending}
            onChange={(e) => setReencrypt(e.target.checked)}
            aria-describedby={`${id}-why`}
            className="mt-0.5 size-5 shrink-0"
          />
          <span>Also re-encrypt this vault</span>
        </label>
        <ReencryptWhy id={`${id}-why`} who={label} />
        {reencrypt && <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />}
        {revoke.isPending && reencrypt && revoked && <p className="text-muted">Re-encrypting; this can take a while for a large vault.</p>}
        {revoke.isError && (
          <FormError>{revoked ? `${label}'s access is revoked, but ${vault} wasn't re-encrypted: ${errorText(revoke.error)}` : errorText(revoke.error)}</FormError>
        )}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            {revoked ? "Close" : "Cancel"}
          </button>
          <button type="submit" disabled={revoke.isPending || (reencrypt && !password)} className={`${primaryButtonClass} flex-1`}>
            {revoked ? "Re-encrypt" : "Revoke"}
          </button>
        </div>
      </form>
    </div>
  );
}

/**
 * Who left the vault since it was last re-encrypted: the owner is offered a re-encryption, as after a revoke (collab
 * design §6.4), and nothing runs unless she asks for it.
 */
function Departures({ vault, left, onDone }: { vault: string; left: Departure[]; onDone(message: string): void }) {
  const id = useId();
  const [asking, setAsking] = useState(false);
  const [password, setPassword] = useState("");
  const rekey = useRekeyVault(vault, password);
  const who = left.map((d) => d.label).join(" or ");
  return (
    <section aria-labelledby={`${id}-title`} className="flex flex-col gap-2 rounded-[12px] border border-line bg-surface-2 p-3">
      <h3 id={`${id}-title`} className="font-medium break-words text-text">
        {left.map((d) => `${d.label} left ${vault} ${timeText(d.leftAt)}.`).join(" ")}
      </h3>
      <ReencryptWhy id={`${id}-why`} who={who} />
      {!asking ? (
        <button type="button" onClick={() => setAsking(true)} aria-describedby={`${id}-why`} className={`${secondaryButtonClass} self-start`}>
          Re-encrypt {vault}…
        </button>
      ) : (
        <form
          aria-busy={rekey.isPending}
          onSubmit={(e) => {
            e.preventDefault();
            if (password)
              rekey.mutate(undefined, {
                onSuccess: (view) => {
                  setPassword("");
                  onDone(`${vault} is re-encrypted.${withdrawnSentence(view)}`);
                },
              });
          }}
          className="flex flex-col gap-2"
        >
          <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />
          {rekey.isPending && <p className="text-muted">Re-encrypting; this can take a while for a large vault.</p>}
          {rekey.isError && <FormError>{errorText(rekey.error)}</FormError>}
          <div className="flex gap-2">
            <button type="button" onClick={() => setAsking(false)} disabled={rekey.isPending} className={secondaryButtonClass}>
              Cancel
            </button>
            <button type="submit" disabled={!password || rekey.isPending} className={primaryButtonClass}>
              Re-encrypt vault
            </button>
          </div>
        </form>
      )}
    </section>
  );
}

/** Raising to maintain asks for the password each time, window open or not; cancelled, the role stays as it was. */
function MaintainDialog({ vault, member, onClose }: { vault: string; member: MemberAccess; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLFormElement>(onClose);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const raise = useRaiseToMaintain(vault, member.id, password);
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-busy={raise.isPending}
        onSubmit={(e) => {
          e.preventDefault();
          if (password) raise.mutate(undefined, { onSuccess: onClose });
        }}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Let {member.label} maintain {vault}?
        </h2>
        <p className="text-warning">{maintainSentence}</p>
        <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />
        {raise.isError && <FormError>{errorText(raise.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={raise.isPending || !password} className={`${primaryButtonClass} flex-1`}>
            Raise to maintain
          </button>
        </div>
      </form>
    </div>
  );
}

/** One person: her role, what she connected, and what the owner can do now. */
function Member({ vault, member, onRevoke }: { vault: string; member: MemberAccess; onRevoke(): void }) {
  const id = useId();
  const change = useMembershipChange(vault);
  const [raising, setRaising] = useState(false);
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
              onChange={(e) => {
                const to = e.target.value as Role;
                if (to === "maintain") setRaising(true);
                else change.mutate({ kind: "role", id: member.id, role: to });
              }}
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
          <button type="button" onClick={onRevoke} className={`${secondaryButtonClass} self-start`}>
            Revoke…
          </button>
        </>
      )}
      {change.isError && <FormError>{errorText(change.error)}</FormError>}
      {raising && <MaintainDialog vault={vault} member={member} onClose={() => setRaising(false)} />}
    </li>
  );
}

/** The owner's view of who her vault is shared with, and the way to invite someone (collab design §8.6, §10.1). */
export default function AccessList({ vault, onClose }: { vault: string; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const access = useVaultAccess(vault, true);
  const queryClient = useQueryClient();
  const [inviting, setInviting] = useState(false);
  // Held here, not by the member's row: the row goes as soon as the revoke is done, and a re-encryption may still run.
  const [revoking, setRevoking] = useState<MemberAccess | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const members = access.data?.members ?? [];
  const left = access.data?.left ?? [];
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
        {done && <p role="status" className="text-text-body">{done}</p>}
        {access.data && members.length === 0 && <p className="text-text-body">{vault} isn't shared with anyone.</p>}
        {members.length > 0 && (
          <ul aria-label="Members" className="flex flex-col gap-2">
            {members.map((m) => (
              <Member
                key={m.id}
                vault={vault}
                member={m}
                onRevoke={() => {
                  setDone(null);
                  setRevoking(m);
                }}
              />
            ))}
          </ul>
        )}
        {left.length > 0 && <Departures vault={vault} left={left} onDone={setDone} />}
        <button type="button" onClick={() => setInviting(true)} className={secondaryButtonClass}>
          Invite someone…
        </button>
        <button type="button" onClick={onClose} className={primaryButtonClass}>
          Done
        </button>
      </div>
      {inviting && (
        <ShareDialog
          vault={vault}
          onClose={() => {
            setInviting(false);
            // Someone may have opened the link meanwhile; the list behind the dialog was asked last when the link was made.
            void queryClient.invalidateQueries({ queryKey: ["vault-access", vault] });
          }}
        />
      )}
      {revoking && (
        <RevokeDialog
          vault={vault}
          member={revoking}
          onDone={(message) => {
            setDone(message);
            setRevoking(null);
          }}
          onClose={() => setRevoking(null)}
        />
      )}
    </div>
  );
}
