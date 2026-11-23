// SPDX-License-Identifier: AGPL-3.0-or-later
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

const changes = (n: number) => (n === 1 ? "1 change" : `${n} changes`);

/**
 * Signing out offline with changes waiting (offline mode design §3a): they stay sealed and upload at the next
 * sign-in here, or go with the copy. Escape stays, as the safe answer.
 */
export default function SignOutDialog({ waiting, onStay, onSignOut }: { waiting: number; onStay(): void; onSignOut(removeCopy: boolean): void }) {
  const dialog = useDialog<HTMLDivElement>(onStay);
  const lead = waiting === 1 ? "1 change hasn't uploaded yet. It stays" : `${changes(waiting)} haven't uploaded yet. They stay`;
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div ref={dialog} role="alertdialog" aria-modal="true" aria-labelledby="sign-out-waiting" className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5">
        <h2 id="sign-out-waiting" className="text-lg font-bold text-text">
          Sign out with changes waiting?
        </h2>
        <p className="text-text-body">{`${lead} on this device, sealed, and upload${waiting === 1 ? "s" : ""} the next time you sign in here.`}</p>
        <div className="flex gap-3">
          <button type="button" onClick={onStay} className={`${secondaryButtonClass} flex-1`}>
            Stay
          </button>
          <button type="button" onClick={() => onSignOut(false)} className={`${primaryButtonClass} flex-1`}>
            Sign out
          </button>
        </div>
        <button type="button" onClick={() => onSignOut(true)} className="flex min-h-11 flex-col items-start self-start text-left text-sm text-text-body underline-offset-4 hover:underline">
          Sign out and remove the copy
          <span className="text-muted">{waiting === 1 ? "The change is then lost." : `The ${waiting} changes are then lost.`}</span>
        </button>
      </div>
    </div>
  );
}
