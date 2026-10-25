// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/admin-client/src/routes/users/index.tsx)
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import type { components } from "../../api/schema";
import ConfirmTyped from "../../components/ConfirmTyped";
import { FormError, secondaryButtonClass } from "../../components/form";
import { useSession } from "../../state/session";
import AdminOnly, { useIsAdmin } from "./AdminOnly";

type AdminUser = components["schemas"]["AdminUserView"];

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const date = (iso: string) => day.format(new Date(iso));
const roleText: Record<string, string> = { user: "User", admin: "Admin", primary_admin: "Primary admin" };

/** Bytes as KB or MB with one decimal; a few bytes stay bytes. */
export function storageText(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** Everyone on the server, as metadata only: no note, no vault name. */
export default function Users() {
  const users = useQuery({ queryKey: ["admin", "users"], queryFn: () => call(api.GET("/api/admin/users")), enabled: useIsAdmin() === true });
  const me = useSession((s) => s.user?.id);
  const [target, setTarget] = useState<AdminUser | null>(null);
  const [deleting, setDeleting] = useState<AdminUser | null>(null);

  return (
    <AdminOnly title="Users">
      {users.isError && <FormError>{errorText(users.error)}</FormError>}
      {users.data && (
        <ul className="flex flex-col gap-3">
          {users.data.map((user) => (
            <UserItem key={user.id} user={user} self={user.id === me} onToggle={() => setTarget(user)} onDelete={() => setDeleting(user)} />
          ))}
        </ul>
      )}
      {target && <ToggleDialog user={target} onClose={() => setTarget(null)} />}
      {deleting && <DeleteDialog user={deleting} onClose={() => setDeleting(null)} />}
    </AdminOnly>
  );
}

function useUpdate(user: AdminUser, onDone?: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: components["schemas"]["UpdateUserRequest"]) =>
      call(api.PATCH("/api/admin/users/{id}", { params: { path: { id: user.id } }, body })),
    onSuccess: async () => {
      // A changed role may be the caller's own view of the world.
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["admin", "users"] }), queryClient.invalidateQueries({ queryKey: ["session"] })]);
      onDone?.();
    },
  });
}

function UserItem({ user, self, onToggle, onDelete }: { user: AdminUser; self: boolean; onToggle(): void; onDelete(): void }) {
  const update = useUpdate(user);
  const primary = user.role === "primary_admin";
  const makeAdmin = user.role !== "admin";

  return (
    <li aria-label={user.display} className={`flex flex-col gap-2 rounded-[12px] border border-line bg-surface p-4${user.active ? "" : " opacity-75"}`}>
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="min-w-0 font-medium break-words text-text">{user.display}</h2>
        <span className="text-sm text-text-body">{roleText[user.role] ?? user.role}</span>
      </div>
      <p className="text-sm break-all text-muted">@{user.username}</p>
      <p className="text-sm text-text-body">{user.active ? "Active" : "Deactivated"}</p>
      <p className="text-sm text-muted">
        Created {date(user.createdAt)} · {user.lastSignInAt ? `Last sign-in ${date(user.lastSignInAt)}` : "Never signed in"}
      </p>
      <p className="flex gap-3 text-sm text-text-body">
        <span>
          {user.vaults} {user.vaults === 1 ? "vault" : "vaults"}
        </span>
        <span>{storageText(Number(user.bytesStored))}</span>
      </p>
      {update.isError && <FormError>{errorText(update.error)}</FormError>}
      {!primary && (
        <div className="flex flex-wrap gap-2">
          {/* Another admin can demote you; doing it to yourself by one click would lock you out of this page. */}
          {!(self && !makeAdmin) && (
            <button
              type="button"
              disabled={update.isPending}
              onClick={() => update.mutate({ role: makeAdmin ? "admin" : "user" })}
              aria-label={`Make ${user.display} ${makeAdmin ? "an admin" : "a user"}`}
              className={secondaryButtonClass}
            >
              {makeAdmin ? "Make admin" : "Make user"}
            </button>
          )}
          {/* The server refuses deactivating oneself, so the button isn't offered. */}
          {!self && (
            <button type="button" onClick={onToggle} aria-label={`${user.active ? "Deactivate" : "Reactivate"} ${user.display}`} className={secondaryButtonClass}>
              {user.active ? "Deactivate" : "Reactivate"}
            </button>
          )}
          {/* Only a deactivated user can be deleted: two deliberate steps, the first of which can be undone. */}
          {!self && !user.active && (
            <button type="button" onClick={onDelete} aria-label={`Delete ${user.display}`} className={secondaryButtonClass}>
              Delete
            </button>
          )}
        </div>
      )}
    </li>
  );
}

function DeleteDialog({ user, onClose }: { user: AdminUser; onClose(): void }) {
  const queryClient = useQueryClient();
  const remove = useMutation({
    mutationFn: () => call(api.POST("/api/admin/users/{id}/delete", { params: { path: { id: user.id } }, body: { confirm: user.username } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["admin", "users"] });
      onClose();
    },
  });
  return (
    <ConfirmTyped
      title={`Delete ${user.display}?`}
      text={`Their account goes for good, with ${user.vaults === 1 ? "their vault" : `their ${user.vaults} vaults`} and every note in them, the history, trash and checkpoints, and their API keys. Nobody can bring it back. The invitations they made stay.`}
      word={user.username}
      action="Delete"
      pending={remove.isPending}
      error={remove.isError ? errorText(remove.error) : null}
      onConfirm={() => remove.mutate()}
      onClose={onClose}
    />
  );
}

function ToggleDialog({ user, onClose }: { user: AdminUser; onClose(): void }) {
  const update = useUpdate(user, onClose);
  const action = user.active ? "Deactivate" : "Reactivate";
  return (
    <ConfirmTyped
      title={`${action} ${user.display}?`}
      text={
        user.active
          ? "They are signed out at once and can't sign in until you reactivate them. Their notes stay."
          : "They can sign in again."
      }
      word={user.username}
      action={action}
      pending={update.isPending}
      error={update.isError ? errorText(update.error) : null}
      onConfirm={() => update.mutate({ active: !user.active })}
      onClose={onClose}
    />
  );
}
