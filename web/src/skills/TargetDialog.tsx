// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState } from "react";
import type { VaultView } from "../api/notes";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { addSkill, copySkill, moveSkill, type SkillView } from "../api/skills";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/** What the server takes as `from`: the skill's folder, or `system:<name>` for a system skill. */
export const sourceOf = (skill: SkillView) => skill.path ?? `system:${skill.name}`;

/** The notice of Add to my skills (skills design §3.12): a copy, never updated by itself. */
export const addNotice = "This adds a copy. For your safety it is never updated automatically; you'll be told when there's a newer version and can review it first.";

type Props = { vaults: VaultView[] | undefined; onClose(): void } & (
  | { mode: "copy" | "move"; skill: SkillView }
  // Add to my skills: the skill behind a link's code, whose name the server knows.
  | { mode: "add"; code: string; onAdded(path: string): void }
);

/**
 * Where a skill goes with Copy to…, Move to… or Add to my skills: one of the vaults she may write, Skills first; another
 * name once the folder's is taken.
 */
export default function TargetDialog(props: Props) {
  const { mode, vaults, onClose } = props;
  const skill = props.mode === "add" ? null : props.skill;
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLDivElement>(onClose);
  // Its own place is no target: the server refuses a skill that is where it would go.
  const targets = (vaults ?? []).filter((v) => v.canWrite && v.name !== skill?.location);
  const [chosen, setTo] = useState<string | null>(null);
  // Skills unless she chose; the vaults may arrive after the dialog opens.
  const to = chosen ?? (targets.find((v) => v.kind === "skills") ?? targets.find((v) => v.name === "Skills") ?? targets[0])?.name ?? "";
  const [name, setName] = useState(skill?.name ?? "");
  const [taken, setTaken] = useState(false);

  const run = useMutation({
    mutationFn: () => {
      const other = taken ? name.trim() : undefined;
      return props.mode === "add" ? addSkill(props.code, to, other) : (props.mode === "copy" ? copySkill : moveSkill)(sourceOf(props.skill), to, other);
    },
    onSuccess: async (view) => {
      // The skill's folder appears in (or leaves) the trees, and the vaults' note counts change.
      await Promise.all(["skills", "tree", "vaults"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
      if (props.mode === "add") props.onAdded(view.path);
      else onClose();
    },
    // Another error leaves the name field and what was typed in it.
    onError: (e) => e instanceof HttpError && e.type === "name-taken" && setTaken(true),
  });

  const verb = mode === "copy" ? "Copy" : mode === "move" ? "Move" : "Add";
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          {skill ? `${verb} ${skill.name} to…` : "Add to my skills"}
        </h2>
        {!vaults ? (
          <p className="text-text-body">Loading…</p>
        ) : targets.length === 0 ? (
          <p className="text-text-body">There is no {skill ? "other " : ""}vault you may write.</p>
        ) : (
          <fieldset className="flex flex-col gap-1">
            <legend className={labelClass}>Vault</legend>
            {targets.map((v) => (
              <label key={v.name} className="flex min-h-11 items-center gap-2 text-text">
                <input
                  type="radio"
                  name={`${id}-vault`}
                  value={v.name}
                  checked={to === v.name}
                  onChange={() => {
                    setTo(v.name);
                    setTaken(false);
                  }}
                />
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
            <input
              id={`${id}-name`}
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              className={inputClass}
            />
          </div>
        )}
        {mode === "move" && skill && (
          <p className="text-text-body">The skill leaves {skill.location === "system" ? "the system" : skill.location}; its files go to that vault's trash.</p>
        )}
        {mode === "add" && <p className="text-text-body">{addNotice}</p>}
        {run.isError && !(run.error instanceof HttpError && run.error.type === "name-taken") && <FormError>{errorText(run.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button
            type="button"
            disabled={run.isPending || !to || (taken && name.trim() === "")}
            onClick={() => run.mutate()}
            className={`${primaryButtonClass} flex-1`}
          >
            {verb}
          </button>
        </div>
      </div>
    </div>
  );
}
