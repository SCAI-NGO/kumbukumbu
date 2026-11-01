// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { errorText } from "../api/error-text";
import { levelOf, ownerLabel, useEmbeddingProviders, useVaults, type Level, type VaultView } from "../api/notes";
import { noteUrl, vaultOf } from "../api/paths";
import { useLeave, useRenameAlias } from "../api/sharing";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import { maintainSentence } from "../routes/rights";
import { movedPath, useEditor } from "../state/editor";

const levelLabels: Record<Level, string> = { read: "Read", write: "Read and write", maintain: "Maintain", owner: "Owner" };

/** The member's level in words: her role, or why it is read-only for everyone just now. */
function LevelText({ vault }: { vault: VaultView }) {
  const level = levelOf(vault);
  if (vault.closingAt) return <>Read only: the vault is closing</>;
  if (vault.ownerInactive) return <>Read only while the owner's account is deactivated</>;
  return level === "maintain" ? <span className="text-warning">{levelLabels[level]}</span> : <>{levelLabels[level]}</>;
}

/**
 * "Leave <vault>?": at once, for her and every key and app she gave it. Notes she wrote stay in the vault; the owner can
 * invite her again. Away from the vault afterwards, if one of its notes was open.
 */
export function LeaveVaultDialog({ vault, membership, onClose, onLeft }: { vault: string; membership: string; onClose(): void; onLeft?(): void }) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const navigate = useNavigate();
  const location = useLocation();
  const leave = useLeave();
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
          Leave {vault}?
        </h2>
        <p className="text-text-body">
          You, and every key and app you gave {vault}, lose access at once. What you wrote stays in the vault. Only its owner can invite you again.
        </p>
        {leave.isError && <FormError>{errorText(leave.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button
            type="button"
            disabled={leave.isPending}
            onClick={() =>
              leave.mutate(
                { membership, vault },
                {
                  onSuccess: () => {
                    if (location.pathname.startsWith(noteUrl(`/${vault}/`))) navigate("/", { replace: true });
                    onLeft?.();
                    onClose();
                  },
                },
              )
            }
            className={`${primaryButtonClass} flex-1`}
          >
            Leave vault
          </button>
        </div>
      </div>
    </div>
  );
}

/**
 * A vault shared with the caller, as she sees it (collab design §8.6, §10.1): its owner, her role, where its notes go
 * for search by meaning; her own name for it, which she can change; and the way out.
 */
export default function MemberPanel({ vault: name, onClose }: { vault: string; onClose(): void }) {
  const vaults = useVaults();
  const vault = vaults.data?.find((v) => v.name === name);
  return vault?.membership ? <Panel vault={vault} membership={vault.membership} onClose={onClose} /> : null;
}

function Panel({ vault, membership, onClose }: { vault: VaultView; membership: string; onClose(): void }) {
  const name = vault.name;
  const id = useId();
  const providers = useEmbeddingProviders();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const navigate = useNavigate();
  const rename = useRenameAlias();
  const [alias, setAlias] = useState(name);
  const [leaving, setLeaving] = useState(false);
  const provider = providers.data?.find((p) => p.name === vault.embeddings);
  const changed = alias.normalize("NFC").trim() !== name && alias.trim() !== "";

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!changed) return;
    const to = alias.normalize("NFC").trim();
    const open = useEditor.getState().currentPath;
    rename.mutate(
      { membership, vault: name, alias: to },
      {
        onSuccess: () => {
          if (open && vaultOf(open) === name) navigate(noteUrl(movedPath(open, `/${name}/`, `/${to}/`)), { replace: true });
          onClose();
        },
      },
    );
  };

  return (
    <div className="fixed inset-0 z-40 flex items-end justify-center bg-black/55 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="k-sheet flex max-h-dvh w-full max-w-md flex-col gap-4 overflow-y-auto rounded-t-[16px] border border-line bg-surface p-4 pb-[max(20px,env(safe-area-inset-bottom))] sm:rounded-[16px]"
      >
        <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
          {name}
        </h2>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-text-body">
          <dt className={labelClass}>Shared by</dt>
          <dd className="min-w-0 break-words">{ownerLabel(vault)}</dd>
          <dt className={labelClass}>Your role</dt>
          <dd>
            <LevelText vault={vault} />
          </dd>
          <dt className={labelClass}>Search by meaning</dt>
          <dd className="min-w-0 break-words">
            {provider?.label ?? vault.embeddings}
            {provider?.external && <span className="block text-sm text-muted">Its notes go to this provider; so do your searches that take it in.</span>}
          </dd>
        </dl>
        {levelOf(vault) === "maintain" && !vault.closingAt && !vault.ownerInactive && <p className="text-sm text-warning">{maintainSentence}</p>}

        <form onSubmit={submit} className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-alias`} className={labelClass}>
            Your name for it
          </label>
          <div className="flex gap-2">
            <input
              id={`${id}-alias`}
              value={alias}
              onChange={(e) => {
                setAlias(e.target.value);
                rename.reset();
              }}
              autoComplete="off"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              className={`${inputClass} min-w-0 flex-1`}
            />
            <button type="submit" disabled={!changed || rename.isPending} className={secondaryButtonClass}>
              Rename
            </button>
          </div>
          <p className="text-sm text-muted">Only you see this name. The owner and others keep theirs.</p>
          {rename.isError && <FormError>{errorText(rename.error)}</FormError>}
        </form>

        <div className="flex gap-2">
          <button type="button" onClick={() => setLeaving(true)} className={secondaryButtonClass}>
            Leave this vault
          </button>
          <button type="button" onClick={onClose} className={`${primaryButtonClass} flex-1`}>
            Done
          </button>
        </div>
      </div>
      {leaving && <LeaveVaultDialog vault={name} membership={membership} onClose={() => setLeaving(false)} onLeft={onClose} />}
    </div>
  );
}
