// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import type { components } from "../api/schema";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { timeText } from "../components/Sheet";
import { CopyButton } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";
import ConnectorReveal from "./ConnectorReveal";
import NewConnectionDialog from "./NewConnectionDialog";
import { grant, RightsPicker, type Right } from "./NewKeyDialog";
import { RightText } from "./rights";
import { inUsageOrder, UsageOrderSwitch, type UsageOrder } from "./usageOrder";

type Connection = components["schemas"]["ConnectionView"];

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const date = (iso: string) => day.format(new Date(iso));

/** Why the server ended or interrupted a connection, in the words the user would use. */
const reasons: Record<string, string> = {
  revoked: "you revoked it",
  vault_deleted: "its vaults were deleted",
  password_changed: "your password was changed",
  recovery: "your account was recovered",
  deactivated: "your account was deactivated",
  refresh_reused: "a token was used twice, which can mean it was stolen",
  code_reused: "a sign-in code was used twice",
  client_revoked: "the app disconnected",
  rights_changed: "its rights were changed",
  rekeyed: "a vault was re-encrypted",
};

function statusText(c: Connection) {
  // A reason this app does not know yet is left out rather than shown raw.
  const reason = c.reason ? reasons[c.reason] : undefined;
  switch (c.state) {
    case "waiting":
      return "Waiting for the app";
    case "connected":
      return "Connected";
    case "needs_reconnect":
      return `Needs reconnect${reason ? `: ${reason}` : ""}`;
    case "revoked":
      return `Revoked${reason ? `: ${reason}` : ""}`;
    case "expired":
      return "Expired";
    default:
      return c.state;
  }
}

const isOver = (c: Connection) => c.state === "revoked" || c.state === "expired";

/** The user's connections for apps that sign in with OAuth: made here, their address shown, changed and revoked here. */
export default function Connections({ onRevealChange }: { onRevealChange?(revealing: boolean): void }) {
  const connections = useQuery({ queryKey: ["connections"], queryFn: () => call(api.GET("/api/connections")) });
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<Connection | null>(null);
  const [changing, setChanging] = useState<Connection | null>(null);
  const [revoking, setRevoking] = useState<Connection | null>(null);
  const [order, setOrder] = useState<UsageOrder>("created");

  const reveal = (c: Connection | null) => {
    setCreated(c);
    onRevealChange?.(c !== null);
  };

  if (created) return <ConnectorReveal connection={created} onDone={() => reveal(null)} />;

  const live = inUsageOrder(connections.data?.filter((c) => !isOver(c)) ?? [], order);
  const over = inUsageOrder(connections.data?.filter(isOver) ?? [], order);

  const item = (c: Connection) => <ConnectionItem key={c.id} connection={c} onChange={() => setChanging(c)} onRevoke={() => setRevoking(c)} />;

  return (
    <section aria-labelledby="connections-title" className="flex min-w-0 flex-col gap-6">
      <h2 id="connections-title" className="text-[22px] font-bold text-text">
        Connections
      </h2>
      <p className="text-text-body">
        A connection lets an app such as Claude (claude.ai, Desktop and the mobile apps) read or write the vaults you choose. Each connection has its own address.
      </p>
      <button type="button" onClick={() => setCreating(true)} className={primaryButtonClass}>
        Connect an app
      </button>

      {connections.isError && <FormError>{errorText(connections.error)}</FormError>}
      {connections.data?.length === 0 && <p className="text-muted">You have no connections yet.</p>}
      {connections.data && connections.data.length > 1 && <UsageOrderSwitch label="Sort connections" order={order} onOrder={setOrder} />}
      {live.length > 0 && <ul className="flex flex-col gap-3">{live.map(item)}</ul>}
      {over.length > 0 && (
        <details className="flex flex-col gap-3">
          <summary className="flex min-h-11 cursor-pointer items-center text-text-body">Revoked and expired ({over.length})</summary>
          <ul className="mt-2 flex flex-col gap-3">{over.map(item)}</ul>
        </details>
      )}

      {creating && (
        <NewConnectionDialog
          onClose={() => setCreating(false)}
          onCreated={(view) => {
            setCreating(false);
            reveal(view);
          }}
        />
      )}
      {changing && <RightsDialog connection={changing} onClose={() => setChanging(null)} />}
      {revoking && <RevokeDialog connection={revoking} onClose={() => setRevoking(null)} />}
    </section>
  );
}

function ConnectionItem({ connection: c, onChange, onRevoke }: { connection: Connection; onChange(): void; onRevoke(): void }) {
  const over = isOver(c);
  const expiry = c.expiresAt ? `${c.state === "expired" ? "Expired" : "Expires"} ${date(c.expiresAt)}` : "Doesn't expire";
  return (
    <li className={`flex min-w-0 flex-col gap-2 rounded-[12px] border border-line bg-surface p-4${over ? " opacity-75" : ""}`}>
      <h3 className="min-w-0 font-medium break-words text-text">{c.name}</h3>
      {c.client && (
        <p className="text-sm break-words text-text-body">
          {c.client.name} · {c.client.host}
          {!c.client.verified && " · self-registered"}
        </p>
      )}
      {/* The address is long: it wraps anywhere, and its button stays within reach of the thumb. */}
      {!over && (
        <div className="flex flex-col gap-2">
          <code className="rounded-[8px] border border-line bg-surface-2 p-2 font-mono text-xs break-all text-text-body">{c.url}</code>
          <CopyButton value={c.url} label="Copy" name={`Copy address of ${c.name}`} className={`${secondaryButtonClass} self-start`} onResult={() => {}} />
        </div>
      )}
      <ul className="flex flex-col text-sm text-text-body">
        {Object.entries(c.vaults).map(([vault, right]) => (
          <li key={vault} className="break-words">
            {vault}: <RightText right={right} />
          </li>
        ))}
      </ul>
      <p className="text-sm text-muted">
        Created {date(c.createdAt)} · {c.lastUsedAt ? `Last used ${timeText(c.lastUsedAt)}` : "Never used"} · {expiry}
      </p>
      <p className="text-sm text-text">{statusText(c)}</p>
      {c.state === "needs_reconnect" && <p className="text-sm text-muted">Use Reconnect in the app.</p>}
      {!over && (
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={onChange} aria-label={`Change rights of ${c.name}`} className={secondaryButtonClass}>
            Change rights
          </button>
          <button type="button" onClick={onRevoke} aria-label={`Revoke ${c.name}`} className={secondaryButtonClass}>
            Revoke
          </button>
        </div>
      )}
    </li>
  );
}

/** New rights per vault. The server ends the app's sign-in, so it has to reconnect. */
function RightsDialog({ connection, onClose }: { connection: Connection; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  const vaults = useVaults();
  const [chosen, setChosen] = useState<Record<string, Right>>(connection.vaults as Record<string, Right>);
  const granted = grant(chosen);

  const save = useMutation({
    mutationFn: () => call(api.PUT("/api/connections/{id}/vaults", { params: { path: { id: connection.id } }, body: { vaults: granted } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["connections"] });
      onClose();
    },
  });

  return (
    <div className="fixed inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <form
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        onSubmit={(e) => {
          e.preventDefault();
          if (Object.keys(granted).length > 0) save.mutate();
        }}
        className="k-sheet flex max-h-dvh w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
          Change rights
        </h2>
        <p className="break-words text-text-body">{connection.name}</p>
        <RightsPicker vaults={vaults} chosen={chosen} onChange={setChosen}>
          The app has to reconnect after this.
        </RightsPicker>
        {save.isError && <FormError>{errorText(save.error)}</FormError>}
        <div className="flex gap-2">
          <button type="button" onClick={onClose} className={secondaryButtonClass}>
            Cancel
          </button>
          <button type="submit" disabled={save.isPending || Object.keys(granted).length === 0} className={primaryButtonClass}>
            Save
          </button>
        </div>
      </form>
    </div>
  );
}

/** "Revoke this connection?": Escape cancels. The app loses access at once and the address is spent. */
function RevokeDialog({ connection, onClose }: { connection: Connection; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const revoke = useMutation({
    mutationFn: () => call(api.DELETE("/api/connections/{id}", { params: { path: { id: connection.id } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["connections"] });
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
          Revoke {connection.name}?
        </h2>
        <p className="text-text-body">The app loses access at once. Its address can't be used again; make a new connection instead.</p>
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
