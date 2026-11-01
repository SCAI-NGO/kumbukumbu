// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { errorText } from "../api/error-text";
import { ownerAtStart, useEmbeddingProviders, useVaults, type VaultView } from "../api/notes";
import { useDismissNotice, useMemberNotices, useProviderSeen } from "../api/sharing";
import { FormError, secondaryButtonClass } from "../components/form";
import { LeaveVaultDialog } from "./MemberPanel";

const bannerClass = "mx-3 mb-2 flex flex-col gap-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text";

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "long", year: "numeric" });

/**
 * The owner chose another embedding provider (collab design §8.5): the member's searches leave the vault out until she
 * OKs it, and she may leave instead. It stays until one of the two.
 */
function ProviderBanner({ vault }: { vault: VaultView }) {
  const providers = useEmbeddingProviders();
  const seen = useProviderSeen();
  const [leaving, setLeaving] = useState(false);
  const provider = providers.data?.find((p) => p.name === vault.embeddings);
  return (
    <section aria-label="Embedding provider changed" className={bannerClass}>
      <p>
        {ownerAtStart(vault)} changed how <em>{vault.name}</em> is searched by meaning: its notes now go to <em>{provider?.label ?? vault.embeddings}</em>. Until you OK
        this, your searches leave <em>{vault.name}</em> out; reading and writing go on.
      </p>
      {seen.isError && <FormError>{errorText(seen.error)}</FormError>}
      <div className="flex gap-2">
        <button
          type="button"
          disabled={seen.isPending}
          onClick={() => seen.mutate({ membership: vault.membership!, provider: vault.embeddings })}
          className={secondaryButtonClass}
        >
          OK
        </button>
        <button type="button" onClick={() => setLeaving(true)} className={secondaryButtonClass}>
          Leave this vault
        </button>
      </div>
      {leaving && <LeaveVaultDialog vault={vault.name} membership={vault.membership!} onClose={() => setLeaving(false)} />}
    </section>
  );
}

/**
 * What a member is told of a vault shared with her, above its notes: a changed embedding provider, the closing period
 * after the owner's deletion (§7.3), the owner deactivated (§7.4). With `refusal`, the open note can't be changed, and
 * this says why where no banner does.
 */
export default function SharedBanner({ vault: name, refusal }: { vault: string | null; refusal?: string | null }) {
  const vaults = useVaults();
  const vault = name ? vaults.data?.find((v) => v.name === name) : undefined;
  if (!vault) return null;
  if (vault.closingAt) {
    return (
      <p role="status" className={bannerClass}>
        The owner deleted their account; {vault.name} will be deleted on {day.format(new Date(vault.closingAt))}. Export a copy to keep its notes.
      </p>
    );
  }
  if (vault.ownerInactive) {
    return (
      <p role="status" className={bannerClass}>
        {ownerAtStart(vault)}'s account is deactivated. This vault is read-only until it is active again. You can export a copy.
      </p>
    );
  }
  return (
    <>
      {vault.providerChanged && vault.membership && <ProviderBanner vault={vault} />}
      {refusal && <p className="mx-3 mb-2 text-sm text-muted">{refusal}</p>}
    </>
  );
}

/** Notices of shared vaults the owner deleted for everyone, by the member's own name for them; each until dismissed. */
export function MemberNotices() {
  const notices = useMemberNotices();
  const dismiss = useDismissNotice();
  if (!notices.data?.length) return null;
  return (
    <ul aria-label="Notices" className="flex flex-col">
      {notices.data.map((n) => (
        <li key={n.id} className={`${bannerClass} flex-row items-center justify-between`}>
          <span className="min-w-0 break-words">{n.text}</span>
          <button type="button" disabled={dismiss.isPending} onClick={() => dismiss.mutate(n.id)} aria-label={`Dismiss: ${n.text}`} className={secondaryButtonClass}>
            Dismiss
          </button>
        </li>
      ))}
    </ul>
  );
}
