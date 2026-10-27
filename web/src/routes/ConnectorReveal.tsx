// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import type { components } from "../api/schema";
import { labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { CopyButton } from "../components/shownOnce";

type Connection = components["schemas"]["ConnectionView"];

/**
 * A new connection's connector URL with what to do with it. The URL is no secret (it only says which connection
 * is meant; the app still needs the user's consent), so unlike a key there is no leave guard.
 */
export default function ConnectorReveal({ connection, onDone }: { connection: Connection; onDone(): void }) {
  const id = useId();
  const [copyFailed, setCopyFailed] = useState(false);

  return (
    <section aria-labelledby={`${id}-title`} className="flex min-w-0 flex-col gap-5">
      <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
        Connect “{connection.name}”
      </h2>

      <div className="flex flex-col gap-1.5">
        <p className={labelClass}>Connector URL</p>
        <pre
          aria-label="Connector URL"
          className="rounded-[12px] border border-line bg-surface p-4 font-mono text-sm leading-relaxed break-all whitespace-pre-wrap text-text select-all"
        >
          {connection.url}
        </pre>
        <CopyButton value={connection.url} label="Copy" className={secondaryButtonClass} onResult={(ok) => setCopyFailed(!ok)} />
        {copyFailed && (
          <p aria-live="polite" className="text-sm text-text">
            Copying didn't work. Select the address and copy it by hand.
          </p>
        )}
      </div>

      <ul className="flex flex-col gap-2 text-text-body">
        <li>1. In claude.ai: Settings → Connectors → Add custom connector.</li>
        <li>2. Paste this URL. Leave client ID and secret empty.</li>
        <li>3. Tip: name the connector like this connection, e.g. 'Kumbu {connection.name}'.</li>
      </ul>
      <p className="text-text-body">Other apps: add a remote MCP server with this URL; it signs in with OAuth.</p>
      <p className="text-sm text-muted">The address is not a secret: it only says which connection is meant. Access needs your consent in Kumbu².</p>

      <button type="button" onClick={onDone} className={primaryButtonClass}>
        Done
      </button>
    </section>
  );
}
