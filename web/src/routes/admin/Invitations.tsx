// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/admin-client/src/routes/invitations/index.tsx)
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import type { components } from "../../api/schema";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { useDialog } from "../../hooks/useDialog";
import AdminOnly from "./AdminOnly";
import InvitationReveal from "./InvitationReveal";
import NewInvitationDialog from "./NewInvitationDialog";

type Invitation = components["schemas"]["AdminInvitationView"];

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const date = (iso: string) => day.format(new Date(iso));
const stateText: Record<string, string> = { open: "Open", used: "Used", expired: "Expired", revoked: "Revoked" };
const forWhom = (role: string) => `for ${role === "admin" ? "an admin" : "a user"}`;

/** The invitations made so far, and the way to make another. */
export default function Invitations() {
  const invitations = useQuery({ queryKey: ["admin", "invitations"], queryFn: () => call(api.GET("/api/admin/invitations")) });
  const [creating, setCreating] = useState(false);
  // The link lives here, in component state only, until the reveal is done.
  const [link, setLink] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<Invitation | null>(null);

  if (link) {
    return (
      <AdminOnly title="Invitations">
        <InvitationReveal link={link} onDone={() => setLink(null)} />
      </AdminOnly>
    );
  }

  return (
    <AdminOnly title="Invitations">
      <button type="button" onClick={() => setCreating(true)} className={primaryButtonClass}>
        New invitation
      </button>

      {invitations.isError && <FormError>{errorText(invitations.error)}</FormError>}
      {invitations.data?.length === 0 && <p className="text-muted">No invitations yet.</p>}
      {invitations.data && invitations.data.length > 0 && (
        <ul className="flex flex-col gap-3">
          {invitations.data.map((invitation) => (
            <li
              key={invitation.id}
              aria-label={`Invitation ${forWhom(invitation.role)}`}
              className={`flex flex-col gap-2 rounded-[12px] border border-line bg-surface p-4${invitation.state === "open" ? "" : " opacity-75"}`}
            >
              <div className="flex items-baseline justify-between gap-3">
                <h2 className="font-medium text-text">{invitation.role === "admin" ? "Admin" : "User"}</h2>
                <span className="text-sm text-text-body">{stateText[invitation.state] ?? invitation.state}</span>
              </div>
              {invitation.note && <p className="break-words text-text-body">{invitation.note}</p>}
              <p className="text-sm text-muted">
                Created {date(invitation.createdAt)} · Expires {date(invitation.expiresAt)}
                {invitation.usedAt && ` · Used ${date(invitation.usedAt)}`}
              </p>
              {invitation.state === "open" && (
                <button
                  type="button"
                  onClick={() => setRevoking(invitation)}
                  aria-label={`Revoke invitation ${forWhom(invitation.role)}`}
                  className={`${secondaryButtonClass} self-start`}
                >
                  Revoke
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {creating && (
        <NewInvitationDialog
          onClose={() => setCreating(false)}
          onCreated={(created) => {
            setCreating(false);
            setLink(created);
          }}
        />
      )}
      {revoking && <RevokeDialog invitation={revoking} onClose={() => setRevoking(null)} />}
    </AdminOnly>
  );
}

/** "Revoke this invitation?": Escape cancels. The link stops working at once. */
function RevokeDialog({ invitation, onClose }: { invitation: Invitation; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const revoke = useMutation({
    mutationFn: () => call(api.DELETE("/api/admin/invitations/{id}", { params: { path: { id: invitation.id } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["admin", "invitations"] });
      onClose();
    },
  });

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold text-text">
          Revoke this invitation?
        </h2>
        <p className="text-text-body">The link stops working at once. This can't be undone.</p>
        {revoke.isError && <FormError>{errorText(revoke.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={revoke.isPending} onClick={() => revoke.mutate()} className={`${primaryButtonClass} flex-1`}>
            Revoke
          </button>
        </div>
      </div>
    </div>
  );
}
