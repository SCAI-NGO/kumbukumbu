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
import DeletionReceipt, { type Receipt } from "./DeletionReceipt";

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
  const [promoting, setPromoting] = useState<AdminUser | null>(null);
  // One receipt after the other: a repeat after a restore gives one per user.
  const [receipts, setReceipts] = useState<Receipt[]>([]);
  const [repeating, setRepeating] = useState(false);
  const deletions = useQuery({
    queryKey: ["admin", "deletions"],
    queryFn: () => call(api.GET("/api/admin/deletions")),
    enabled: useIsAdmin() === true,
  });
  const restored = deletions.data?.restored ?? [];

  return (
    <AdminOnly title="Users">
      {deletions.isError && <FormError>{errorText(deletions.error)}</FormError>}
      {deletions.data?.configured === false && (
        <p className="mb-4 text-sm text-text-body">
          Deleted users aren't listed outside the database (Kumbu:Deletions:Path isn't set), so a backup restore that brings one back won't be noticed.
        </p>
      )}
      {restored.length > 0 && (
        <div role="status" className="mb-4 flex flex-col gap-3 rounded-[12px] border border-line bg-surface p-4 text-text">
          <p>
            A backup restore brought back {restored.length} deleted user(s): {restored.map((u) => u.username).join(", ")}. Delete them again?
          </p>
          <div>
            <button type="button" onClick={() => setRepeating(true)} className={secondaryButtonClass}>
              Delete again…
            </button>
          </div>
        </div>
      )}
      {users.isError && <FormError>{errorText(users.error)}</FormError>}
      {users.data && (
        <ul className="flex flex-col gap-3">
          {users.data.map((user) => (
            <UserItem
              key={user.id}
              user={user}
              self={user.id === me}
              onToggle={() => setTarget(user)}
              onDelete={() => setDeleting(user)}
              onPromote={() => setPromoting(user)}
            />
          ))}
        </ul>
      )}
      {target && <ToggleDialog user={target} onClose={() => setTarget(null)} />}
      {promoting && <PromoteDialog user={promoting} onClose={() => setPromoting(null)} />}
      {deleting && (
        <DeleteDialog
          user={deleting}
          onClose={() => setDeleting(null)}
          onDeleted={(r) => {
            setDeleting(null);
            setReceipts([r]);
          }}
        />
      )}
      {repeating && (
        <RepeatDialog
          count={restored.length}
          onClose={() => setRepeating(false)}
          onDeleted={(rs) => {
            setRepeating(false);
            setReceipts(rs);
          }}
        />
      )}
      {receipts.length > 0 && <DeletionReceipt key={receipts[0].userId} receipt={receipts[0]} onClose={() => setReceipts((rs) => rs.slice(1))} />}
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

function UserItem({ user, self, onToggle, onDelete, onPromote }: { user: AdminUser; self: boolean; onToggle(): void; onDelete(): void; onPromote(): void }) {
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
              onClick={() => (makeAdmin ? onPromote() : update.mutate({ role: "user" }))}
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
          {/* Any user but oneself (Settings → delete account) and the primary admin, active or not, in one step. */}
          {!self && (
            <button type="button" onClick={onDelete} className={secondaryButtonClass}>
              Delete for a privacy request…
            </button>
          )}
        </div>
      )}
    </li>
  );
}

function DeleteDialog({ user, onClose, onDeleted }: { user: AdminUser; onClose(): void; onDeleted(receipt: Receipt): void }) {
  const queryClient = useQueryClient();
  // The admin's own password, read through the closure: the mutation cache keeps no variables of it.
  const [password, setPassword] = useState("");
  const remove = useMutation({
    mutationFn: () => call(api.POST("/api/admin/users/{id}/delete", { params: { path: { id: user.id } }, body: { password, confirm: user.username } })),
    onSuccess: async (receipt) => {
      onDeleted(receipt);
      await queryClient.invalidateQueries({ queryKey: ["admin"] });
    },
  });
  return (
    <ConfirmTyped
      title={`Delete ${user.display} for a privacy request?`}
      text="Before you go on: make sure the request really comes from this person, and answer within one month (GDPR Art. 12(3)). Their account goes for good, with every vault and note, the history, trash and checkpoints, their API keys and app connections. Nobody can bring it back. Backups are not reached."
      word={user.username}
      action="Delete"
      pending={remove.isPending}
      error={remove.isError ? errorText(remove.error) : null}
      onConfirm={() => remove.mutate()}
      onClose={onClose}
      password={password}
      onPasswordChange={setPassword}
    />
  );
}

/** After a restore: deletes again every user it brought back, with the admin's password (the list names them). */
function RepeatDialog({ count, onClose, onDeleted }: { count: number; onClose(): void; onDeleted(receipts: Receipt[]): void }) {
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const repeat = useMutation({
    mutationFn: () => call(api.POST("/api/admin/deletions/repeat", { body: { password } })),
    onSuccess: async (receipts) => {
      onDeleted(receipts);
      await queryClient.invalidateQueries({ queryKey: ["admin"] });
    },
  });
  return (
    <ConfirmTyped
      title={`Delete ${count} restored user(s) again?`}
      text="They were deleted before, and a backup restore brought their accounts back. Everything of theirs goes again, for good, as the first time."
      action="Delete again"
      pending={repeat.isPending}
      error={repeat.isError ? errorText(repeat.error) : null}
      onConfirm={() => repeat.mutate()}
      onClose={onClose}
      password={password}
      onPasswordChange={setPassword}
    />
  );
}

/** Making someone an admin takes the admin's own password, as inviting one does: a stolen session alone can't. */
function PromoteDialog({ user, onClose }: { user: AdminUser; onClose(): void }) {
  const update = useUpdate(user, onClose);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  return (
    <ConfirmTyped
      title={`Make ${user.display} an admin?`}
      text="An admin can invite, deactivate and delete accounts here. Your password confirms it."
      action="Make admin"
      pending={update.isPending}
      error={update.isError ? errorText(update.error) : null}
      onConfirm={() => update.mutate({ role: "admin", password })}
      onClose={onClose}
      password={password}
      onPasswordChange={setPassword}
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
