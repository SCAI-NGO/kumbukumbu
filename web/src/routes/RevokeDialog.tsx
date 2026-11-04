// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { components } from "../api/schema";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

type ApiKey = components["schemas"]["ApiKeyView"];

/** "Revoke this key?": Escape cancels. Whatever uses the key stops working at once. */
export default function RevokeDialog({ apiKey, onClose }: { apiKey: ApiKey; onClose(): void }) {
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
        <p className="text-text-body">
          {apiKey.kind === "device" ? "This device stops syncing at once." : "Agents using this key lose access at once."} This can't be undone.
        </p>
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
