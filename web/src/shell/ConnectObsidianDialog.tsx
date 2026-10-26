// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation } from "@tanstack/react-query";
import { useEffect, useId, useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { CopyButton } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";
import { ExpiryPicker } from "../routes/NewKeyDialog";

/** What to add in BRAT; when the plugin is in Obsidian's community directory, the dialog says to search for it there. */
export const pluginRepo = "SCAI-NGO/kumbu-obsidian";

const copyClass = "min-h-11 flex-none rounded-[10px] border border-line px-3 text-sm font-medium text-text-body focus-visible:outline-2 focus-visible:outline-accent";

/** "9:41": minutes and seconds left, never below 0:00. */
function left(ms: number) {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/**
 * Pairing a vault with Obsidian (sync spec §3): the plugin and the server to type in, how long the device's key
 * lasts, and a code valid once for ten minutes, which the plugin exchanges for that key. The code lives in this
 * component only; a new one is asked for on opening, on "New code" and when the expiry changes.
 */
export default function ConnectObsidianDialog({ vault, onClose }: { vault: string; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const server = window.location.origin;
  const [expiresInDays, setExpiresInDays] = useState(0);
  const [now, setNow] = useState(() => Date.now());
  const pairing = useMutation({
    mutationFn: (days: number) =>
      call(api.POST("/api/vaults/{vault}/pairing", { params: { path: { vault } }, body: { expiresInDays: days === 0 ? null : days } })),
  });
  const { mutate } = pairing;

  useEffect(() => mutate(expiresInDays), [mutate, expiresInDays]);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  const code = pairing.data && !pairing.isPending ? pairing.data : null;
  const remaining = code ? Date.parse(code.expiresAt) - now : 0;
  const valid = code !== null && remaining > 0;

  return (
    <div className="absolute inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <div>
          <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
            Connect "{vault}" with Obsidian
          </h2>
          <p className="mt-1 text-text-body">Works with an empty Obsidian vault or one that already has notes.</p>
        </div>

        <ol className="flex flex-col gap-3 text-text-body">
          <li className="flex flex-col gap-1.5">
            <p>
              <span className="font-medium text-text">1.</span> In Obsidian, add the plugin with BRAT (Community plugins → BRAT → Add beta plugin):
            </p>
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 truncate rounded-[10px] border border-line bg-surface-2 px-3 py-2.5 font-mono text-sm text-text">{pluginRepo}</code>
              <CopyButton value={pluginRepo} label="Copy" name="Copy the plugin's name" className={copyClass} onResult={() => {}} />
            </div>
          </li>
          <li className="flex flex-col gap-1.5">
            <p>
              <span className="font-medium text-text">2.</span> In its settings (Kumbu² Sync), enter this server:
            </p>
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 truncate rounded-[10px] border border-line bg-surface-2 px-3 py-2.5 font-mono text-sm text-text">{server}</code>
              <CopyButton value={server} label="Copy" name="Copy the server address" className={copyClass} onResult={() => {}} />
            </div>
          </li>
          <li className="flex flex-col gap-1.5">
            <p>
              <span className="font-medium text-text">3.</span> And this pairing code, then Connect:
            </p>
            <div className="flex flex-col items-center gap-1 rounded-[12px] border border-line bg-surface-2 p-4">
              {valid ? (
                <>
                  <p className={labelClass}>Pairing code</p>
                  <p className="font-mono text-[32px] font-bold tracking-[0.12em] text-text">{code.code}</p>
                  <p className="text-sm text-muted">valid for {left(remaining)} · single use</p>
                </>
              ) : (
                pairing.isPending && <p className="text-sm text-muted">Asking for a code…</p>
              )}
              {pairing.isError && <FormError>{errorText(pairing.error)}</FormError>}
              <button type="button" onClick={() => mutate(expiresInDays)} disabled={pairing.isPending} className={`${secondaryButtonClass} mt-1`}>
                New code
              </button>
            </div>
          </li>
        </ol>

        <ExpiryPicker value={expiresInDays} onChange={setExpiresInDays}>
          The device's key. A new choice makes a new code.
        </ExpiryPicker>

        <p className="text-sm text-muted">Each device gets its own key, named "{vault} – Obsidian &lt;device&gt;". You can revoke it under API keys.</p>

        <button type="button" onClick={onClose} className={primaryButtonClass}>
          Done
        </button>
      </div>
    </div>
  );
}
