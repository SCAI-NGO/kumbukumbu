// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { checkpointUrl } from "../api/paths";
import { errorText } from "../api/error-text";
import { useCheckpoints, useCreateCheckpoint, usePreviewCheckpoint, useRemoveCheckpoint, useRestoreCheckpoint, type CheckpointEntry, type RestorePreview } from "../api/history";
import { checkpointExportUrl, download } from "../data/exportUrl";
import { useEditor } from "../state/editor";
import { FormError, inputClass, labelClass, secondaryButtonClass } from "../components/form";
import { Sheet, timeText } from "../components/Sheet";
import { ago } from "../note/AgentBanner";

/** As the server's rule: letters, digits, '.', '-' and '_', starting with a letter or digit, at most 64. */
const NAME = /^[\p{L}\p{N}][\p{L}\p{N}._-]{0,63}$/u;

const smallButton =
  "min-h-11 flex-1 rounded-[10px] border border-line px-3 text-sm font-medium text-accent-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";
const dangerButton =
  "min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent";

type Step = { kind: "list" } | { kind: "restore"; checkpoint: CheckpointEntry } | { kind: "remove"; checkpoint: CheckpointEntry };

/**
 * The vault's named checkpoints: make one of the whole vault, restore one (after a preview of exactly what it
 * changes), remove one (for good). One dialog for every step, so the focus stays in it.
 */
export default function CheckpointsSheet({ vault, onClose }: { vault: string; onClose(): void }) {
  const [step, setStep] = useState<Step>({ kind: "list" });
  const [done, setDone] = useState<string | null>(null);
  const back = () => setStep({ kind: "list" });

  if (step.kind === "restore") {
    return (
      <Sheet title={`Restore ${step.checkpoint.name}?`} onClose={onClose} onBack={back} backLabel="All checkpoints">
        <RestoreStep
          vault={vault}
          checkpoint={step.checkpoint}
          onBack={back}
          onDone={(message) => {
            setDone(message);
            back();
          }}
        />
      </Sheet>
    );
  }
  if (step.kind === "remove") {
    return (
      <Sheet title={`Remove ${step.checkpoint.name}?`} onClose={onClose} onBack={back} backLabel="All checkpoints">
        <RemoveStep
          vault={vault}
          checkpoint={step.checkpoint}
          onBack={back}
          onDone={() => {
            setDone(`Removed checkpoint ${step.checkpoint.name}. The notes are as they were.`);
            back();
          }}
        />
      </Sheet>
    );
  }
  return (
    <Sheet title="Checkpoints" onClose={onClose}>
      <ListStep vault={vault} onClose={onClose} done={done} onRestore={(checkpoint) => setStep({ kind: "restore", checkpoint })} onRemove={(checkpoint) => setStep({ kind: "remove", checkpoint })} />
    </Sheet>
  );
}

function ListStep({ vault, onClose, done, onRestore, onRemove }: { vault: string; onClose(): void; done: string | null; onRestore(c: CheckpointEntry): void; onRemove(c: CheckpointEntry): void }) {
  const id = useId();
  const navigate = useNavigate();
  const checkpoints = useCheckpoints(vault);
  const create = useCreateCheckpoint(vault);
  const [name, setName] = useState("");
  const [problem, setProblem] = useState<string | null>(null);
  const list = checkpoints.data?.checkpoints ?? [];
  const limit = Number(checkpoints.data?.limit ?? 0);
  const full = !!checkpoints.data && list.length >= limit;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setProblem(null);
    const wanted = name.trim();
    if (!NAME.test(wanted)) return setProblem("A name has letters, digits, “.”, “-” and “_”, starts with a letter or digit, and has at most 64 characters.");
    if (list.some((c) => c.name === wanted)) return setProblem(`There is a checkpoint named “${wanted}” already.`);
    create.mutate(wanted, { onSuccess: () => setName("") });
  };

  return (
    <>
      <p className="text-sm text-text-body">
        A checkpoint keeps the state of every note in {vault} under a name, until you remove it. Unlike the history, it never runs out. Open one to read it as it was, or export it as a ZIP.
      </p>
      {done && (
        <p role="status" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
          {done}
        </p>
      )}
      <form onSubmit={submit} className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-name`} className={labelClass}>
          New checkpoint
        </label>
        <div className="flex gap-2">
          <input
            id={`${id}-name`}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="before-cleanup"
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            maxLength={64}
            disabled={full}
            className={`${inputClass} min-w-0 flex-1`}
          />
          <button type="submit" disabled={create.isPending || full || name.trim() === ""} className={`${secondaryButtonClass} flex-none`}>
            Make
          </button>
        </div>
        {full && <p className="text-sm text-muted">{vault} has {limit} checkpoints, the most it keeps. Remove one to make another.</p>}
        {problem && <FormError>{problem}</FormError>}
        {create.isError && <FormError>{errorText(create.error)}</FormError>}
      </form>
      {checkpoints.isPending && <p className="text-sm text-muted">Loading the checkpoints …</p>}
      {checkpoints.isError && <FormError>{errorText(checkpoints.error)}</FormError>}
      {checkpoints.data && (
        <p className="text-sm text-muted">
          {list.length} of {limit} checkpoints
        </p>
      )}
      {list.length > 0 && (
        <ul aria-label="Checkpoints" className="flex flex-col gap-1.5">
          {list.map((c) => (
            <li key={c.name} className="flex flex-col gap-1.5 rounded-[12px] border border-line bg-surface-2 px-3.5 py-2.5">
              <div className="flex items-baseline gap-2">
                <span className="min-w-0 flex-1 truncate font-mono font-medium text-text">{c.name}</span>
                <span className="flex-none text-[12px] text-dim">{ago(c.createdAt)}</span>
              </div>
              <p className="text-sm text-muted">
                {c.scope} · {Number(c.notes) === 1 ? "1 note" : `${c.notes} notes`} · {timeText(c.createdAt)} by {c.createdBy}
              </p>
              <div className="flex gap-2">
                <button
                  type="button"
                  aria-label={`Open ${c.name}`}
                  onClick={() => {
                    onClose();
                    useEditor.getState().setOverlay("none");
                    navigate(checkpointUrl(vault, c.name));
                  }}
                  className={smallButton}
                >
                  Open
                </button>
                <button type="button" aria-label={`Export ${c.name} as ZIP`} onClick={() => download(checkpointExportUrl(vault, c.name))} className={smallButton}>
                  Export as ZIP
                </button>
              </div>
              <div className="flex gap-2">
                <button type="button" aria-label={`Restore ${c.name}`} onClick={() => onRestore(c)} className={smallButton}>
                  Restore…
                </button>
                <button type="button" aria-label={`Remove ${c.name}`} onClick={() => onRemove(c)} className={smallButton}>
                  Remove…
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

const count = (n: number, noun: string) => (n === 1 ? `1 ${noun}` : `${n} ${noun}s`);

/** What the preview says, note by note: what happens to it. */
function changeText(c: RestorePreview["changes"][number]) {
  const lines = c.added !== null && c.removed !== null ? ` (+${c.added} −${c.removed} lines)` : "";
  switch (c.kind) {
    case "changed":
      return `gets its text from then${lines}`;
    case "back":
      return "comes back (it was removed since)";
    case "trash":
      return "goes to the trash (it was made since)";
    default:
      return `moves back from ${c.from}${lines}`;
  }
}

function RestoreStep({ vault, checkpoint, onBack, onDone }: { vault: string; checkpoint: CheckpointEntry; onBack(): void; onDone(message: string): void }) {
  const preview = usePreviewCheckpoint(vault, checkpoint.name);
  const restore = useRestoreCheckpoint(vault, checkpoint.name);
  const data = preview.data;
  const changes = data?.changes ?? [];
  const rewritten = Number(data?.rewrittenLinks ?? 0);

  return (
    <>
      <p className="text-sm text-text-body">
        Made {timeText(checkpoint.createdAt)} by {checkpoint.createdBy}, of {checkpoint.scope}.
      </p>
      {preview.isPending && <p className="text-sm text-muted">Working out what would change …</p>}
      {preview.isError && <FormError>{errorText(preview.error)}</FormError>}
      {data && changes.length === 0 && <p className="text-text-body">{data.scope} already matches this checkpoint. There is nothing to restore.</p>}
      {data && changes.length > 0 && (
        <>
          <div role="note" className="flex flex-col gap-2 rounded-[12px] border border-accent bg-surface-2 p-3 text-text-body">
            <p>
              <strong className="text-text">Restoring makes {data.scope} match the checkpoint exactly.</strong> {count(changes.length, "note")} change:
            </p>
            <ul aria-label="What changes" className="flex max-h-60 flex-col gap-1 overflow-y-auto">
              {changes.map((c) => (
                <li key={`${c.kind}-${c.path}`} className="text-sm">
                  <span className="font-mono text-[12px] text-text">{c.path}</span> {changeText(c)}
                </li>
              ))}
            </ul>
            {rewritten > 0 && (
              <p className="text-sm">
                {count(rewritten, "link")} in {data.rewrittenIn.join(", ")} follow the notes that move back.
              </p>
            )}
            {data.broken.length > 0 && (
              <div className="text-sm">
                <p>{data.broken.length === 1 ? "Afterwards, this link outside would lead nowhere:" : "Afterwards, these links outside would lead nowhere:"}</p>
                <ul className="mt-1 flex flex-col gap-0.5 font-mono text-[12px]">
                  {data.broken.map((b) => (
                    <li key={b}>{b}</li>
                  ))}
                </ul>
              </div>
            )}
            <p className="text-sm">
              Nothing is lost: each note keeps the text it had before in its history, and notes going to the trash stay there for 30 days. If a note changes
              before you confirm, nothing is restored.
            </p>
          </div>
          {restore.isError && <FormError>{errorText(restore.error)}</FormError>}
        </>
      )}
      <div className="flex gap-2.5">
        <button type="button" onClick={onBack} className={`${secondaryButtonClass} flex-1`}>
          Cancel
        </button>
        {data?.plan && (
          <button type="button" disabled={restore.isPending} onClick={() => restore.mutate(data.plan!, { onSuccess: (r) => onDone(sentence(r.message)) })} className={dangerButton}>
            Restore {count(changes.length, "note")}
          </button>
        )}
      </div>
    </>
  );
}

/** The server's lower-case message as a sentence. */
const sentence = (message: string) => message[0].toUpperCase() + message.slice(1) + ".";

function RemoveStep({ vault, checkpoint, onBack, onDone }: { vault: string; checkpoint: CheckpointEntry; onBack(): void; onDone(): void }) {
  const remove = useRemoveCheckpoint(vault);
  return (
    <>
      <p role="note" className="rounded-[12px] border border-accent bg-surface-2 p-3 text-text-body">
        <strong className="text-text">Removing a checkpoint is permanent:</strong> it can't be brought back, and neither can the state it kept. The notes
        themselves stay as they are.
      </p>
      {remove.isError && <FormError>{errorText(remove.error)}</FormError>}
      <div className="flex gap-2.5">
        <button type="button" onClick={onBack} className={`${secondaryButtonClass} flex-1`}>
          Cancel
        </button>
        <button type="button" disabled={remove.isPending} onClick={() => remove.mutate(checkpoint.name, { onSuccess: onDone })} className={dangerButton}>
          Remove for good
        </button>
      </div>
    </>
  );
}
