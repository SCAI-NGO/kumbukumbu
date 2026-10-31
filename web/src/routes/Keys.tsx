// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { components } from "../api/schema";
import { AccountScreen, FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { timeText } from "../components/Sheet";
import { useDialog } from "../hooks/useDialog";
import Connections from "./Connections";
import KeyReveal from "./KeyReveal";
import NewKeyDialog from "./NewKeyDialog";
import { RightText } from "./rights";
import { inUsageOrder, UsageOrderSwitch, type UsageOrder } from "./usageOrder";

type ApiKey = components["schemas"]["ApiKeyView"];

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const date = (iso: string) => day.format(new Date(iso));

/** The user's API keys for agents: made here, shown once, revoked here. */
export default function Keys() {
  const keys = useQuery({ queryKey: ["keys"], queryFn: () => call(api.GET("/api/keys")) });
  const [creating, setCreating] = useState(false);
  // The new key lives here, in component state only, until the reveal is done.
  const [created, setCreated] = useState<{ name: string; key: string } | null>(null);
  const [revoking, setRevoking] = useState<ApiKey | null>(null);
  // While a connector URL is shown, it has the page to itself, as a new key has.
  const [revealing, setRevealing] = useState(false);
  const [order, setOrder] = useState<UsageOrder>("created");

  if (created) {
    return (
      <AccountScreen title="Access">
        <KeyReveal name={created.name} value={created.key} onDone={() => setCreated(null)} />
      </AccountScreen>
    );
  }

  return (
    <AccountScreen title="Access">
      {!revealing && (
        <section aria-labelledby="keys-title" className="flex min-w-0 flex-col gap-6">
          <h2 id="keys-title" className="text-[22px] font-bold text-text">
            API keys
          </h2>
          <p className="text-text-body">An API key lets an agent such as Claude Code read or write the vaults you give it, through MCP.</p>
          <button type="button" onClick={() => setCreating(true)} className={primaryButtonClass}>
            New key
          </button>

          {keys.isError && <FormError>{errorText(keys.error)}</FormError>}
          {keys.data?.length === 0 && <p className="text-muted">You have no API keys yet.</p>}
          {keys.data && keys.data.length > 1 && <UsageOrderSwitch label="Sort API keys" order={order} onOrder={setOrder} />}
          {keys.data && keys.data.length > 0 && (
            <ul className="flex flex-col gap-3">
              {inUsageOrder(keys.data, order).map((key) => (
                <KeyItem key={key.id} apiKey={key} onRevoke={() => setRevoking(key)} />
              ))}
            </ul>
          )}
        </section>
      )}
      <Connections onRevealChange={setRevealing} />

      {creating && (
        <NewKeyDialog
          onClose={() => setCreating(false)}
          onCreated={({ key, apiKey }) => {
            setCreating(false);
            setCreated({ name: apiKey.name, key });
          }}
        />
      )}
      {revoking && <RevokeDialog apiKey={revoking} onClose={() => setRevoking(null)} />}
    </AccountScreen>
  );
}

/** "Expires …", "Expired …" or "Doesn't expire"; nothing for a revoked key, whose expiry no longer matters. */
function expiryText(apiKey: ApiKey, expired: boolean) {
  if (apiKey.revokedAt !== null) return "";
  if (!apiKey.expiresAt) return " · Doesn't expire";
  return ` · ${expired ? "Expired" : "Expires"} ${date(apiKey.expiresAt)}`;
}

function KeyItem({ apiKey, onRevoke }: { apiKey: ApiKey; onRevoke(): void }) {
  const revoked = apiKey.revokedAt !== null;
  const expired = apiKey.expiresAt !== null && new Date(apiKey.expiresAt).getTime() <= Date.now();
  return (
    <li className={`flex flex-col gap-2 rounded-[12px] border border-line bg-surface p-4${revoked || expired ? " opacity-75" : ""}`}>
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="min-w-0 font-medium break-words text-text">
          {apiKey.name}
          {apiKey.kind === "device" && <span className="ml-2 rounded-full border border-line px-2 py-0.5 text-[12px] font-normal text-muted">Obsidian device</span>}
        </h2>
        <span className="font-mono text-sm text-muted">{apiKey.prefix}…</span>
      </div>
      <ul className="flex flex-col text-sm text-text-body">
        {Object.entries(apiKey.vaults).map(([vault, right]) => (
          <li key={vault}>
            {vault}: <RightText right={right} />
          </li>
        ))}
      </ul>
      <p className="text-sm text-muted">
        Created {date(apiKey.createdAt)} · {apiKey.lastUsedAt ? `Last used ${timeText(apiKey.lastUsedAt)}` : "Never used"}
        {revoked && ` · Revoked ${date(apiKey.revokedAt!)}`}
        {expiryText(apiKey, expired)}
      </p>
      {/* An expired key opens nothing already; revoking it would change nothing. */}
      {!revoked && !expired && (
        <button type="button" onClick={onRevoke} aria-label={`Revoke ${apiKey.name}`} className={`${secondaryButtonClass} self-start`}>
          Revoke
        </button>
      )}
    </li>
  );
}

/** "Revoke this key?": Escape cancels. Agents using the key stop working at once. */
function RevokeDialog({ apiKey, onClose }: { apiKey: ApiKey; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const revoke = useMutation({
    mutationFn: () => call(api.DELETE("/api/keys/{id}", { params: { path: { id: apiKey.id } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["keys"] });
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
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Revoke {apiKey.name}?
        </h2>
        <p className="text-text-body">Agents using this key lose access at once. This can't be undone.</p>
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
