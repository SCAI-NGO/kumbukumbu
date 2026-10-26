// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import type { components } from "../../api/schema";
import { primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { CopyButton } from "../../components/shownOnce";
import { useDialog } from "../../hooks/useDialog";

export type Receipt = components["schemas"]["DeletionReceiptView"];

/** `2026-10-27T10:15:00Z` as `2026-10-27 10:15 UTC`: the same in every time zone, as a record should be. */
function utcText(iso: string) {
  return `${new Date(iso).toISOString().slice(0, 16).replace("T", " ")} UTC`;
}

/**
 * The receipt an admin keeps as a record of a deletion for a privacy request: ids, usernames, the time and counts.
 * Never content or a vault or note name: the server doesn't send any, and nothing here adds one.
 */
export function receiptText(r: Receipt): string {
  const providers =
    r.externalProviders.length === 0
      ? ["External embedding providers: none configured"]
      : ["External embedding providers:", ...r.externalProviders.map((p) => `  ${p}: may have received text from vaults that used it; no copy was deleted there`)];
  return [
    "Deleted for a privacy request",
    "",
    `Time: ${utcText(r.deletedAt)}`,
    `Username: ${r.username}`,
    `User id: ${r.userId}`,
    `Deleted by: ${r.deletedBy}`,
    `Vaults: ${r.vaults}`,
    `Notes: ${r.notes}`,
    `API keys (incl. revoked): ${r.apiKeys}`,
    `App connections (incl. revoked): ${r.connections}`,
    ...(Number(r.invitations) > 0 ? [`Invitation they joined with: kept as used, its note and link to them erased (${r.invitations})`] : []),
    ...providers,
    "Backups are not reached by this deletion.",
    ...(r.listed ? [] : ["Not recorded in the deletion list (it isn't configured or couldn't be written)."]),
    "",
  ].join("\n");
}

/** Only when the id is on the deletion list can the server notice a restore: otherwise the reply promises nothing about it. */
const listedPromise = " If a backup is ever restored, we delete your account again.";

/** The reply the admin sends from the privacy contact: neutral, English, and signed with the configured contact. */
export function replyText(r: Receipt): string {
  return `Subject: Your deletion request

Hello,

as you requested, we deleted your Kumbu² account "${r.username}" and all notes in it on ${utcText(r.deletedAt)}. This includes every vault, its history, trash and checkpoints, your API keys and your app connections.

Copies that AI tools or other apps read from your notes are outside Kumbu². Please delete them there, for example in your chat history.

Our backups are not changed by a deletion; they are kept as described in our privacy notice.${r.listed ? listedPromise : ""}

In future, if you still have access, you can export your notes first and delete your account yourself in Settings.

Kind regards
${r.privacyContact ?? "[your privacy contact]"}
`;
}

/** Shown once the user is gone: the receipt to download and keep, and the reply to copy into an email. */
export default function DeletionReceipt({ receipt, onClose }: { receipt: Receipt; onClose(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const [copyFailed, setCopyFailed] = useState(false);
  const text = receiptText(receipt);

  function download() {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = `kumbu-deletion-${receipt.username}-${new Date(receipt.deletedAt).toISOString().slice(0, 10)}.txt`;
    link.click();
    // Not in the same tick: Safari can lose the download when the URL goes before it started.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex max-h-full w-full max-w-md flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold text-text">
          Deleted for a privacy request
        </h2>
        <p className="text-text-body">Keep the receipt as your record. Send the reply from your privacy contact.</p>
        <pre className="rounded-[12px] border border-line bg-surface p-4 font-mono text-sm break-words whitespace-pre-wrap text-text">
          {text.split("\n").slice(2).join("\n").trimEnd()}
        </pre>
        <div className="flex flex-wrap gap-3">
          <button type="button" onClick={download} className={`${secondaryButtonClass} flex-1`}>
            Download receipt
          </button>
          <CopyButton value={replyText(receipt)} label="Copy reply" className={`${secondaryButtonClass} flex-1`} onResult={(ok) => setCopyFailed(!ok)} />
        </div>
        {!receipt.listed && (
          <p className="text-sm text-text">
            This deletion isn't on the deletion list, so a restore that brings the account back won't be noticed. The reply leaves out the promise to
            delete it again; keep the user id and check for it after any restore.
          </p>
        )}
        {copyFailed && (
          <p aria-live="polite" className="text-sm text-text">
            Copying didn't work. Download the receipt and write the reply by hand.
          </p>
        )}
        <button type="button" onClick={onClose} className={primaryButtonClass}>
          Close
        </button>
      </div>
    </div>
  );
}
