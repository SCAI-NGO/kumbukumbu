// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults, type VaultView } from "../api/notes";
import type { components } from "../api/schema";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { timeText } from "../components/Sheet";
import DavConnect from "./DavConnect";
import NewShareDialog from "./NewShareDialog";
import RevokeDialog from "./RevokeDialog";
import { CappedRight } from "./rights";
import { inUsageOrder, UsageOrderSwitch, type UsageOrder } from "./usageOrder";
import { useReveal } from "./useReveal";

type DavShare = components["schemas"]["DavShareView"];

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const date = (iso: string) => day.format(new Date(iso));

/**
 * The user's WebDAV shares (WebDAV design §3.2, §3.13): made here, the password shown once on the connect page,
 * revoked here. Listed and made while the server has WebDAV switched off too; they work once it is on.
 */
export default function DavShares({ onRevealChange }: { onRevealChange?(revealing: boolean): void }) {
  const shares = useQuery({ queryKey: ["dav-shares"], queryFn: () => call(api.GET("/api/dav-shares")) });
  const [creating, setCreating] = useState(false);
  // The new password lives here, in component state only, until the connect page is done.
  const [created, reveal] = useReveal<{ share: DavShare; password: string }>(onRevealChange);
  // A share's connect page from its row: no password, which the app doesn't have any more.
  const [connecting, setConnecting] = useState<DavShare | null>(null);
  const [revoking, setRevoking] = useState<DavShare | null>(null);
  const [order, setOrder] = useState<UsageOrder>("created");
  const vaults = useVaults();

  if (created) return <DavConnect share={created.share} password={created.password} onDone={() => reveal(null)} />;
  if (connecting) return <DavConnect share={connecting} onDone={() => setConnecting(null)} />;

  const list = shares.data?.shares;
  return (
    <>
      <section aria-labelledby="dav-title" className="flex min-w-0 flex-col gap-6">
        <h2 id="dav-title" className="text-[22px] font-bold text-text">
          WebDAV
        </h2>
        <p className="text-text-body">
          A WebDAV share mounts the vaults you give it as a drive, in a file manager such as Explorer, Finder or Dolphin, or for rclone and sync
          apps.
        </p>
        {shares.data?.enabled === false && (
          <p role="note" className="text-warning">
            WebDAV is switched off on this server; shares work once it is on.
          </p>
        )}
        <button type="button" onClick={() => setCreating(true)} className={primaryButtonClass}>
          New share
        </button>

        {shares.isError && <FormError>{errorText(shares.error)}</FormError>}
        {list?.length === 0 && <p className="text-muted">You have no WebDAV shares yet.</p>}
        {list && list.length > 1 && <UsageOrderSwitch label="Sort WebDAV shares" order={order} onOrder={setOrder} />}
        {list && list.length > 0 && (
          <ul className="flex flex-col gap-3">
            {inUsageOrder(list, order).map((share) => (
              <ShareItem key={share.id} share={share} vaults={vaults.data} onConnect={() => setConnecting(share)} onRevoke={() => setRevoking(share)} />
            ))}
          </ul>
        )}
      </section>

      {creating && (
        <NewShareDialog
          onClose={() => setCreating(false)}
          onCreated={({ password, share }) => {
            setCreating(false);
            reveal({ share, password });
          }}
        />
      )}
      {revoking && <RevokeDialog share={revoking} onClose={() => setRevoking(null)} />}
    </>
  );
}

/** "Expires …", "Expired …" or "Doesn't expire"; nothing for a revoked share, whose expiry no longer matters. */
function expiryText(share: DavShare, expired: boolean) {
  if (share.revokedAt !== null) return "";
  if (!share.expiresAt) return " · Doesn't expire";
  return ` · ${expired ? "Expired" : "Expires"} ${date(share.expiresAt)}`;
}

function ShareItem({ share, vaults, onConnect, onRevoke }: { share: DavShare; vaults: VaultView[] | undefined; onConnect(): void; onRevoke(): void }) {
  const revoked = share.revokedAt !== null;
  const expired = share.expiresAt !== null && new Date(share.expiresAt).getTime() <= Date.now();
  const live = !revoked && !expired;
  const lost = share.lostVaults.length;
  return (
    <li className={`flex flex-col gap-2 rounded-[12px] border border-line bg-surface p-4${live ? "" : " opacity-75"}`}>
      <div className="flex items-baseline justify-between gap-3">
        <h3 className="min-w-0 font-medium break-words text-text">{share.label}</h3>
        <span className="font-mono text-sm break-all text-muted">{share.username}</span>
      </div>
      <ul className="flex flex-col text-sm text-text-body">
        {Object.entries(share.vaults).map(([vault, right]) => (
          <li key={vault}>
            {vault}: <CappedRight right={right} vault={vaults?.find((v) => v.name === vault)} />
          </li>
        ))}
      </ul>
      {/* The server names lost vaults by id only: the share no longer opens them, so their names aren't hers to read. */}
      {live && lost > 0 && (
        <p role="note" className="text-sm text-warning">
          This share lost {lost === 1 ? "a vault" : `${lost} vaults`} when {lost === 1 ? "it was" : "they were"} re-encrypted; make a new share for{" "}
          {lost === 1 ? "it" : "them"}.
        </p>
      )}
      <p className="text-sm text-muted">
        Created {date(share.createdAt)} · {share.lastUsedAt ? `Last used ${timeText(share.lastUsedAt)}` : "Never used"}
        {revoked && ` · Revoked ${date(share.revokedAt!)}`}
        {expiryText(share, expired)}
      </p>
      {/* A revoked or expired share mounts nothing: neither the steps nor revoking would help. */}
      {live && (
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={onConnect} aria-label={`How to connect ${share.label}`} className={secondaryButtonClass}>
            How to connect
          </button>
          <button type="button" onClick={onRevoke} aria-label={`Revoke ${share.label}`} className={secondaryButtonClass}>
            Revoke
          </button>
        </div>
      )}
    </li>
  );
}
