// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import type { VaultView } from "../api/notes";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { copySkill, moveSkill, type SkillView } from "../api/skills";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/** What the server takes as `from`: the skill's folder, or `system:<name>` for a system skill. */
export const sourceOf = (skill: SkillView) => skill.path ?? `system:${skill.name}`;

/** Where a skill goes with Copy to… or Move to…: one of the vaults she may write, Skills first; another name once the folder's is taken. */
export default function TargetDialog({ skill, mode, vaults, onClose }: { skill: SkillView; mode: "copy" | "move"; vaults: VaultView[]; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLDivElement>(onClose);
  // Its own place is no target: the server refuses a skill that is where it would go.
  const targets = vaults.filter((v) => v.canWrite && v.name !== skill.location);
  const [to, setTo] = useState((targets.find((v) => v.name === "Skills") ?? targets[0])?.name ?? "");
  const [name, setName] = useState(skill.name);
  const [taken, setTaken] = useState(false);

  const run = useMutation({
    mutationFn: () => (mode === "copy" ? copySkill : moveSkill)(sourceOf(skill), to, taken ? name.trim() : undefined),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["skills"] });
      onClose();
    },
    onError: (e) => setTaken(e instanceof HttpError && e.type === "name-taken"),
  });

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby={`${id}-title`} className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5">
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          {mode === "copy" ? "Copy" : "Move"} {skill.name} to…
        </h2>
        {targets.length === 0 ? (
          <p className="text-text-body">There is no other vault you may write.</p>
        ) : (
          <fieldset className="flex flex-col gap-1">
            <legend className={labelClass}>Vault</legend>
            {targets.map((v) => (
              <label key={v.name} className="flex min-h-11 items-center gap-2 text-text">
                <input type="radio" name={`${id}-vault`} value={v.name} checked={to === v.name} onChange={() => setTo(v.name)} />
                {v.name}
              </label>
            ))}
          </fieldset>
        )}
        {taken && (
          <div className="flex flex-col gap-1.5">
            <p className="text-text-body">{to} has a skill of this name already. Choose another name.</p>
            <label htmlFor={`${id}-name`} className={labelClass}>
              Name
            </label>
            <input id={`${id}-name`} value={name} onChange={(e) => setName(e.target.value)} autoCapitalize="none" autoCorrect="off" spellCheck={false} className={inputClass} />
          </div>
        )}
        {mode === "move" && <p className="text-text-body">The skill leaves {skill.location === "system" ? "the system" : skill.location}; its files go to that vault's trash.</p>}
        {run.isError && !taken && <FormError>{errorText(run.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={run.isPending || !to || (taken && name.trim() === "")} onClick={() => run.mutate()} className={`${primaryButtonClass} flex-1`}>
            {mode === "copy" ? "Copy" : "Move"}
          </button>
        </div>
      </div>
    </div>
  );
}
