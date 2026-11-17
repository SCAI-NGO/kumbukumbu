// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import { useVaults } from "../../api/notes";
import { withdrawnSentence } from "../../api/sharing";
import { storageText, useAccountStorage } from "../../api/storage";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { PasswordField } from "../../components/PasswordField";
import { useDialog } from "../../hooks/useDialog";
import PublicationsSection from "../PublicationsSection";
import SharedVaultsSection from "../SharedVaultsSection";
import DeleteVaultDialog from "../../shell/DeleteVaultDialog";
import { Done, Section } from "./Section";

export default function Vaults() {
  return (
    <>
      <VaultsSection />
      <Section title="Shared vaults">
        <SharedVaultsSection />
      </Section>
      <Section title="Published notes">
        <PublicationsSection />
      </Section>
    </>
  );
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** Deleting a vault, with its name typed: the one place in the app that removes notes beyond the trash. */
/** "Storage: 182 MB of 2 GB": what her own vaults take, as the admin's list counts it and the quota limits it. */
function StorageLine() {
  const storage = useAccountStorage();
  if (storage.isError) return <FormError>{errorText(storage.error)}</FormError>;
  if (!storage.data) return null;
  const used = storageText(Number(storage.data.used));
  return (
    <p className="text-text-body">
      {storage.data.limit == null ? `Storage: ${used} (no limit)` : `Storage: ${used} of ${storageText(Number(storage.data.limit))}`}
    </p>
  );
}

function VaultsSection() {
  const vaults = useVaults();
  const [deleting, setDeleting] = useState<string | null>(null);
  const [rekeying, setRekeying] = useState<string | null>(null);
  const [rekeyed, setRekeyed] = useState<string | null>(null);
  return (
    <Section title="Vaults">
      <p className="text-text-body">
        Deleting a vault removes it for good, with every note in it, their history, the trash and the checkpoints. API keys and WebDAV shares keep their other vaults; one that had only this one is revoked.
      </p>
      <p className="text-text-body">
        Re-encrypting a vault gives it a new key. API keys, connected apps and WebDAV shares with access to it lose it and need to be set up or
        reconnected.
      </p>
      <StorageLine />
      <p className="text-sm text-muted">
        Your vaults with their history, trash and files count; so do vaults you share, whoever writes in them. Vaults shared with you count against their owner.
      </p>
      {vaults.isError && <FormError>{errorText(vaults.error)}</FormError>}
      {vaults.data?.length === 0 && <p className="text-muted">You have no vault yet.</p>}
      {vaults.data && vaults.data.length > 0 && (
        <ul className="flex flex-col gap-2">
          {vaults.data.map((vault) => (
            <li key={vault.name} className="flex items-center gap-3">
              <span className="min-w-0 flex-1 break-words text-text">{vault.name}</span>
              <span className="shrink-0 text-sm text-muted">{vault.pending ? "Being re-encrypted" : plural(Number(vault.notes), "note", "notes")}</span>
              <button
                type="button"
                onClick={() => {
                  setRekeyed(null);
                  setRekeying(vault.name);
                }}
                aria-label={`Re-encrypt ${vault.name}`}
                className={`${secondaryButtonClass} shrink-0`}
              >
                Re-encrypt
              </button>
              <button type="button" onClick={() => setDeleting(vault.name)} aria-label={`Delete ${vault.name}`} className={`${secondaryButtonClass} shrink-0`}>
                Delete
              </button>
            </li>
          ))}
        </ul>
      )}
      {rekeyed && <Done>{rekeyed}</Done>}
      {deleting !== null && <DeleteVaultDialog vault={deleting} onClose={() => setDeleting(null)} />}
      {rekeying !== null && <RekeyVaultDialog vault={rekeying} onDone={setRekeyed} onClose={() => setRekeying(null)} />}
    </Section>
  );
}

/** What loses access is listed first; only then is the password asked. The answer waits for the vault to be done. */
function RekeyVaultDialog({ vault, onDone, onClose }: { vault: string; onDone(message: string): void; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const access = useQuery({
    queryKey: ["vault-access", vault],
    queryFn: () => call(api.GET("/api/vaults/{vault}/access", { params: { path: { vault } } })),
    // Always as it is now: a key made a moment ago must be on the list.
    staleTime: 0,
    gcTime: 0,
  });
  const rekey = useMutation({
    mutationFn: () => call(api.POST("/api/vaults/{vault}/rekey", { params: { path: { vault } }, body: { password } })),
    onSuccess: async (view) => {
      setPassword("");
      const keys = Number(view.revokedApiKeys);
      const apps = Number(view.disconnectedApps);
      const shares = Number(view.revokedShares ?? 0);
      onDone(
        (keys + apps === 0
          ? `${vault} is re-encrypted.`
          : `${vault} is re-encrypted. ${plural(keys, "API key was", "API keys were")} revoked and ${plural(apps, "app was", "apps were")} disconnected.`) +
          (shares === 0 ? "" : ` ${plural(shares, "WebDAV share was", "WebDAV shares were")} revoked.`) +
          withdrawnSentence(view),
      );
      await Promise.all(["vaults", "keys", "connections", "dav-shares"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
      onClose();
    },
  });
  const lists = access.data;
  // Members' keys and apps lose the vault too; the owner sees their names under Access already.
  const membersWith = lists?.members.filter((m) => m.credentials.length > 0) ?? [];
  const nothing = lists && lists.apiKeys.length === 0 && lists.connections.length === 0 && lists.shares.length === 0 && membersWith.length === 0;

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-busy={rekey.isPending}
        onSubmit={(e) => {
          e.preventDefault();
          if (lists && password) rekey.mutate();
        }}
        className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Re-encrypt {vault}?
        </h2>
        <p className="text-text-body">The vault gets a new key. What has access to it now loses it and needs to be set up or reconnected.</p>
        {access.isPending && <p className="text-muted">Looking up what has access…</p>}
        {access.isError && <FormError>{errorText(access.error)}</FormError>}
        {nothing && <p className="text-text-body">No API key, connected app or WebDAV share has access to it.</p>}
        {lists && lists.apiKeys.length > 0 && <AccessList title="API keys" names={lists.apiKeys} />}
        {lists && lists.connections.length > 0 && <AccessList title="Connected apps" names={lists.connections} />}
        {lists && lists.shares.length > 0 && <AccessList title="WebDAV shares" names={lists.shares} />}
        {membersWith.length > 0 && (
          <AccessList title="Members' keys and apps" names={membersWith.map((m) => `${m.label}: ${m.credentials.map((c) => c.name).join(", ")}`)} />
        )}
        {lists && <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />}
        {rekey.isPending && <p className="text-muted">Re-encrypting; this can take a while for a large vault.</p>}
        {rekey.isError && <FormError>{errorText(rekey.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={!lists || !password || rekey.isPending} className={`${primaryButtonClass} flex-1`}>
            Re-encrypt vault
          </button>
        </div>
      </form>
    </div>
  );
}

function AccessList({ title, names }: { title: string; names: string[] }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1">
      <h3 id={id} className={labelClass}>
        {title}
      </h3>
      <ul aria-labelledby={id} className="list-disc pl-5 text-text">
        {names.map((name) => (
          <li key={name} className="break-words">
            {name}
          </li>
        ))}
      </ul>
    </div>
  );
}
