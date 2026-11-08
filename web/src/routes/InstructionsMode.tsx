// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { FormError } from "../components/form";

type Mode = "list" | "hint";

/** How the skills reach an agent key's or connection's instructions: listed with a hint, or only the hint. */
export default function InstructionsMode({ kind, id, mode }: { kind: "keys" | "connections"; id: string; mode: string | undefined }) {
  const selectId = useId();
  const queryClient = useQueryClient();
  const save = useMutation({
    mutationFn: (next: Mode) =>
      call(
        kind === "keys"
          ? api.PUT("/api/keys/{id}/instructions", { params: { path: { id } }, body: { mode: next } })
          : api.PUT("/api/connections/{id}/instructions", { params: { path: { id } }, body: { mode: next } }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [kind] }),
  });

  return (
    <div className="flex flex-col gap-1 text-sm">
      <label htmlFor={selectId} className="text-text-body">
        Skills in the instructions
      </label>
      <select
        id={selectId}
        value={mode === "hint" ? "hint" : "list"}
        disabled={save.isPending}
        onChange={(e) => save.mutate(e.target.value as Mode)}
        className="min-h-11 self-start rounded-[8px] border border-line bg-surface-2 px-2 text-text"
      >
        <option value="list">List and hint</option>
        <option value="hint">Hint only</option>
      </select>
      {save.isError && <FormError>{errorText(save.error)}</FormError>}
    </div>
  );
}
