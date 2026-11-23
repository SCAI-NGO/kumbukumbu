// SPDX-License-Identifier: AGPL-3.0-or-later
// The devices that keep an offline copy of the user's notes, each with "Remove offline copy" (design §4.4): in
// Settings, Offline, and right after a password change, as a copy elsewhere still opens with the old password.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { OfflineError, RejectedError } from "eidolon-core/engine/ports";
import { useId, useState, type ReactNode } from "react";
import { errorText } from "../../api/error-text";
import { PasswordField } from "../../components/PasswordField";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { useDialog } from "../../hooks/useDialog";
import { ago } from "../../note/AgentBanner";
import { listDeviceCopies, needsPassword, PasswordNeededError, removeDeviceCopy, type DeviceCopy } from "../../offline/slots";

/** The store's and the slots' errors are the core's, not HttpError: their sentences are shown as errorText shows the API's. */
export function failureText(e: unknown): string {
  if (e instanceof RejectedError) return e.message;
  if (e instanceof OfflineError) return "The server can't be reached right now. Try again in a moment.";
  return errorText(e);
}

export const plural = (n: number, one: string, many: string) => `${n.toLocaleString("en-GB")} ${n === 1 ? one : many}`;
export const changes = (n: number) => plural(n, "change", "changes");

/** The list, and the removal dialog; `othersOnly` leaves this browser's own copy out. */
export function DeviceCopies(props: { othersOnly?: boolean }) {
  const queryClient = useQueryClient();
  const devices = useQuery({ queryKey: ["offline-devices"], queryFn: listDeviceCopies });
  const [removing, setRemoving] = useState<DeviceCopy | null>(null);
  const [password, setPassword] = useState("");
  // The server can ask too: the device reported changes after the list was read.
  const [asked, setAsked] = useState(false);
  const close = () => {
    setRemoving(null);
    setPassword("");
    setAsked(false);
    remove.reset();
  };
  const remove = useMutation({
    mutationFn: (v: { id: string; password?: string }) => removeDeviceCopy(v.id, v.password),
    onSuccess: async () => {
      close();
      await queryClient.invalidateQueries({ queryKey: ["offline-devices"] });
    },
    onError: (e) => {
      if (e instanceof PasswordNeededError) setAsked(true);
    },
  });
  const shown = devices.data?.filter((d) => !props.othersOnly || !d.current);
  const withPassword = removing !== null && (needsPassword(removing) || asked);
  return (
    <>
      {devices.isError && <p className="text-muted">The list needs a connection.</p>}
      {shown?.length === 0 && <p className="text-muted">{props.othersOnly ? "No other device keeps a copy of your notes." : "No device keeps a copy of your notes."}</p>}
      {shown && shown.length > 0 && (
        <ul aria-label="Devices with a copy" className="flex flex-col gap-2">
          {shown.map((d) => (
            <li key={d.id} className="flex min-h-11 items-center gap-3">
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="break-words text-text">{d.label}</span>
                <span className="text-sm text-muted">{deviceDetail(d)}</span>
              </span>
              {!d.current && (
                <button type="button" onClick={() => setRemoving(d)} className={secondaryButtonClass}>
                  Remove offline copy
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {removing && (
        <Confirm
          title={`Remove the copy on ${removing.label}?`}
          text={removalText(removing)}
          action="Remove"
          busy={remove.isPending || (withPassword && password === "")}
          error={remove.isError ? failureText(remove.error) : null}
          onConfirm={() => remove.mutate({ id: removing.id, ...(withPassword ? { password } : {}) })}
          onClose={close}
        >
          {withPassword && <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />}
        </Confirm>
      )}
    </>
  );
}

function removalText(d: DeviceCopy): string {
  const lost = d.waiting > 0
    ? `${changes(d.waiting)} on that device haven't uploaded and will be lost. `
    : d.contactAt === null ? "That device hasn't said yet whether it has changes waiting; any it has will be lost. " : "";
  return `${lost}The copy goes when that device next comes online.${needsPassword(d) ? " Enter your password to remove it." : ""}`;
}

/** "This device", or "last online 3 days ago · had 2 changes waiting". */
function deviceDetail(d: DeviceCopy): string {
  const parts = [d.current ? "This device" : d.contactAt ? `last online ${ago(d.contactAt)}` : `set up ${ago(d.createdAt)}`];
  if (!d.current && d.waiting > 0) parts.push(`had ${changes(d.waiting)} waiting`);
  return parts.join(" · ");
}

export function Confirm(props: {
  title: string; text: ReactNode; action: string; busy: boolean; error: string | null; onConfirm(): void; onClose(): void; children?: ReactNode;
}) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(props.onClose);
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
          {props.title}
        </h2>
        <p className="text-text-body">{props.text}</p>
        {props.children}
        {props.error && <FormError>{props.error}</FormError>}
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={props.onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={props.busy} onClick={props.onConfirm} className={`${primaryButtonClass} flex-1`}>
            {props.action}
          </button>
        </div>
      </div>
    </div>
  );
}
