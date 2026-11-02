// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, type ReactNode } from "react";

/** What becomes of the deleted account's vaults that are shared with others (collab design §7.3); the values are the server's. */
export type SharedVaultsChoice = "give-members-7-days" | "delete-now";

export const defaultSharedVaultsChoice: SharedVaultsChoice = "give-members-7-days";

/**
 * The two choices for an account's shared vaults, "Give members 7 days" first and chosen: for her own dialog and the
 * admin's. `count` is how many vaults the choice is for, so that the sentences speak of one vault or of each.
 */
export default function SharedVaultsChoiceField({
  legend,
  intro,
  count,
  value,
  onChange,
}: {
  legend: string;
  intro: ReactNode;
  count: number;
  value: SharedVaultsChoice;
  onChange(value: SharedVaultsChoice): void;
}) {
  const one = count === 1;
  const name = useId();
  const option = (choice: SharedVaultsChoice, title: string, text: string) => (
    <label className="flex items-start gap-2 text-text-body">
      <input type="radio" name={name} checked={value === choice} onChange={() => onChange(choice)} className="mt-1" />
      <span>
        <b className="text-text">{title}</b>: {text}
      </span>
    </label>
  );
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-1 font-bold text-text">{legend}</legend>
      <div className="text-text-body">{intro}</div>
      {option(
        "give-members-7-days",
        "Give members 7 days",
        `${one ? "it" : "each"} stays read-only for its members for 7 days, so that they can export what they need, and is then deleted.`,
      )}
      {option(
        "delete-now",
        `Delete ${one ? "it" : "them"} for everyone now`,
        `${one ? "it" : "each"} is deleted at once, and each member is told that the owner deleted it.`,
      )}
    </fieldset>
  );
}
