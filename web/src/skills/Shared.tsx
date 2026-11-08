// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import { errorText } from "../api/error-text";
import { updateSharedSkill, useSharedSkills, withdrawSharedSkill, type SharedSkillView } from "../api/skills";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { CopyButton, LeaveDialog, useLeaveGuard } from "../components/shownOnce";
import { useDialog } from "../hooks/useDialog";

const dayAndTime = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });

/** The sentence before a new link for a skill whose old one she withdrew (Decision 15). */
export const sharingAnew = "Copies made from the old link will be told their source is gone and get no more updates.";

/**
 * Her skills shared by link (skills design §3.12): when each copy was taken, Update when the skill changed since, and
 * Withdraw. No link: it was shown once, when the skill was shared, and while it exists no second one is made.
 */
export default function Shared() {
  const shared = useSharedSkills();
  if (shared.isPending) return <p className="text-text-body">Loading…</p>;
  if (shared.isError) return <FormError>{errorText(shared.error)}</FormError>;
  if (shared.data.length === 0) return <p className="text-text-body">No skill is shared by link.</p>;
  return (
    <>
      <p className="text-text-body">A link was shown once, when you shared its skill. To give someone a new one, withdraw the link and share the skill again.</p>
      <ul className="flex flex-col gap-3">
        {shared.data.map((s) => (
          <SharedItem key={s.id} item={s} />
        ))}
      </ul>
    </>
  );
}

function SharedItem({ item }: { item: SharedSkillView }) {
  const queryClient = useQueryClient();
  const [asking, setAsking] = useState(false);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["skills"] });
  const update = useMutation({ mutationFn: () => updateSharedSkill(item.id), onSuccess: refresh });
  const withdraw = useMutation({
    mutationFn: () => withdrawSharedSkill(item.id),
    onSuccess: refresh,
    onSettled: () => setAsking(false),
  });
  const error = update.error ?? withdraw.error;

  return (
    <li className="flex min-w-0 flex-col gap-2 rounded-[12px] border border-line p-3">
      <h3 className="font-mono text-sm font-bold break-words text-text">{item.path}</h3>
      <p className="text-text-body">
        Shared {dayAndTime.format(new Date(item.snapshotAt))}
        {item.changed && " · the skill changed since"}
      </p>
      {error && <FormError>{errorText(error)}</FormError>}
      <div className="flex flex-wrap gap-2">
        {item.changed && (
          <button type="button" disabled={update.isPending} onClick={() => update.mutate()} className={secondaryButtonClass}>
            Update
          </button>
        )}
        <button type="button" onClick={() => setAsking(true)} className={secondaryButtonClass}>
          Withdraw
        </button>
      </div>
      {asking && <ConfirmWithdraw busy={withdraw.isPending} onKeep={() => setAsking(false)} onWithdraw={() => withdraw.mutate()} />}
    </li>
  );
}

function ConfirmWithdraw({ busy, onKeep, onWithdraw }: { busy: boolean; onKeep(): void; onWithdraw(): void }) {
  const dialog = useDialog<HTMLDivElement>(onKeep);
  const id = useId();
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <p id={`${id}-title`} className="text-text-body">
          Withdraw the link? Whoever has it can't open the skill any more, and copies made from it get no more updates.
        </p>
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onKeep} className={`${secondaryButtonClass} flex-1`}>
            Keep
          </button>
          <button type="button" disabled={busy} onClick={onWithdraw} className={`${primaryButtonClass} flex-1`}>
            Withdraw
          </button>
        </div>
      </div>
    </div>
  );
}

/** What a link gives out, said before every Share: the files as they are, a photo's metadata with it (a note's
 * publication strips that, a skill's doesn't, since a skill must work as it was written). */
export const sharingAsIs =
  "Anyone with the link can open the skill and download its files as they are. Images are shared as they are, with any location data and other metadata they carry, such as where a photo was taken.";

/** Before a skill's link: what goes out with it, and, for a skill whose old link she withdrew, what that means for the
 * copies made from the old one. */
export function ConfirmShare({ name, anew, busy, onCancel, onShare }: { name: string; anew: boolean; busy: boolean; onCancel(): void; onShare(): void }) {
  const dialog = useDialog<HTMLDivElement>(onCancel);
  const id = useId();
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-describedby={`${id}-text`}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="font-bold break-words text-text">
          Share “{name}” by link?
        </h2>
        <div id={`${id}-text`} className="flex flex-col gap-2 text-text-body">
          <p>{sharingAsIs}</p>
          {anew && <p>{sharingAnew}</p>}
        </div>
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onCancel} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={busy} onClick={onShare} className={`${primaryButtonClass} flex-1`}>
            {anew ? "Share anew" : "Share"}
          </button>
        </div>
      </div>
    </div>
  );
}

/**
 * A skill's new link, shown once (as a new key, `routes/KeyReveal.tsx`): until it was copied, leaving the page or
 * pressing Done asks once. The link stays in component state only.
 */
export function SkillLinkReveal({ name, link, onDone }: { name: string; link: string; onDone(): void }) {
  const id = useId();
  const [saved, setSaved] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const [closing, setClosing] = useState(false);
  const blocker = useLeaveGuard(saved);

  const copied = (ok: boolean) => {
    setCopyFailed(!ok);
    if (ok) setSaved(true);
  };

  const leave = {
    id: `${id}-leave`,
    title: "You haven't copied the link",
    text: "You won't see this link again. Without it, you'll need to withdraw it and share the skill anew.",
  };

  return (
    <section aria-labelledby={`${id}-title`} className="flex flex-col gap-5">
      <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
        “{name}” is shared
      </h2>
      <p className="text-text-body">This link is shown only now. Copy it and send it to whoever should have the skill.</p>
      <div className="flex flex-col gap-1.5">
        <p className={labelClass}>Link</p>
        <pre
          aria-label="Link"
          className="rounded-[12px] border border-line bg-surface p-4 font-mono text-sm leading-relaxed break-all whitespace-pre-wrap text-text select-all"
        >
          {link}
        </pre>
        <CopyButton value={link} label="Copy link" className={secondaryButtonClass} onResult={copied} />
      </div>
      {copyFailed && (
        <p aria-live="polite" className="text-sm text-text">
          Copying didn't work. Select the link and copy it by hand.
        </p>
      )}
      <button type="button" onClick={() => (saved ? onDone() : setClosing(true))} className={primaryButtonClass}>
        Done
      </button>
      {blocker.state === "blocked" && <LeaveDialog {...leave} onStay={() => blocker.reset?.()} onLeave={() => blocker.proceed?.()} />}
      {closing && <LeaveDialog {...leave} onStay={() => setClosing(false)} onLeave={onDone} />}
    </section>
  );
}
