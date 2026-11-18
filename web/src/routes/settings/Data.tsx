// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import { useVaults } from "../../api/notes";
import { repeats } from "../../components/ConfirmTyped";
import SharedVaultsChoiceField, { defaultSharedVaultsChoice, type SharedVaultsChoice } from "../../components/SharedVaultsChoice";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { PasswordField } from "../../components/PasswordField";
import ExportDialog from "../../data/ExportDialog";
import ImportDialog from "../../data/ImportDialog";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { forgetLastNote } from "../../shell/lastNote";
import { Section } from "./Section";

export default function Data() {
  return (
    <>
      <YourDataSection />
      <DeleteAccountSection />
    </>
  );
}

/** Taking one's notes out (and, later, bringing them in): rarely used, so it lives here and not in the menus. */
export function YourDataSection() {
  const [exporting, setExporting] = useState(false);
  const [importing, setImporting] = useState(false);
  return (
    <Section title="Your data">
      <p className="text-text-body">
        The export is a ZIP with a folder for each vault, with its Markdown notes, PDFs and images; each folder opens in Obsidian as a vault (no history, trash or checkpoints).
        Importing such a ZIP makes a vault of each folder, or merges into the vault of that name.
      </p>
      <div className="flex flex-wrap gap-3">
        <button type="button" onClick={() => setExporting(true)} className={secondaryButtonClass}>
          Export my vaults…
        </button>
        <button type="button" onClick={() => setImporting(true)} className={secondaryButtonClass}>
          Import vaults…
        </button>
      </div>
      {exporting && <ExportDialog onClose={() => setExporting(false)} />}
      {importing && <ImportDialog onClose={() => setImporting(false)} />}
    </Section>
  );
}

/** Needs the password and the username typed out, so neither a stolen session nor a slip deletes the account. */
function DeleteAccountSection() {
  const id = useId();
  const me = useSession((s) => s.user);
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [typed, setTyped] = useState("");
  const [choice, setChoice] = useState<SharedVaultsChoice>(defaultSharedVaultsChoice);
  const vaults = useVaults();
  // Her own vaults with an active member: the ones the server asks about. A vault shared with nobody isn't one.
  const shared = (vaults.data ?? []).filter((v) => v.owner == null && Number(v.members ?? 0) > 0);
  const remove = useMutation({
    mutationFn: () => call(api.POST("/api/account/delete", { body: { password, confirm: me?.username ?? typed, ...(shared.length > 0 && { sharedVaults: choice }) } })),
    onSuccess: () => {
      setPassword("");
      queryClient.clear();
      useEditor.getState().reset();
      forgetLastNote(me?.id);
      useSession.getState().end("Your account is deleted, with all your vaults and notes.");
    },
  });

  if (!me) return null;
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (password && repeats(typed, me.username)) remove.mutate();
  };

  return (
    <Section title="Delete account">
      {me.role === "primary_admin" ? (
        <p className="text-text-body">As the primary admin you can't delete your account: the server always needs one.</p>
      ) : (
        <>
          <p className="text-text-body">
            Deletes your account for good: every vault with all its notes, their history, the trash and the checkpoints, your API keys and your sessions.
            Agents using your keys stop working at once. Nobody can bring it back, not even an admin.
          </p>
          <form onSubmit={submit} className="flex flex-col gap-4">
            {shared.length > 0 && (
              <SharedVaultsChoiceField
                legend="Your shared vaults"
                count={shared.length}
                intro={
                  <>
                    You share {shared.length === 1 ? "this vault" : "these vaults"} with others: <b className="text-text">{shared.map((v) => v.name).join(", ")}</b>.
                    What becomes of {shared.length === 1 ? "it" : "them"}?
                  </>
                }
                value={choice}
                onChange={setChoice}
              />
            )}
            <PasswordField label="Current password" value={password} onChange={setPassword} autoComplete="current-password" />
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-confirm`} className={labelClass}>
                Type <span className="normal-case">{me.username}</span> to confirm
              </label>
              <input
                id={`${id}-confirm`}
                value={typed}
                onChange={(e) => setTyped(e.target.value)}
                autoComplete="off"
                autoCapitalize="none"
                autoCorrect="off"
                spellCheck={false}
                className={inputClass}
              />
            </div>
            {remove.isError && <FormError>{errorText(remove.error)}</FormError>}
            <button type="submit" disabled={remove.isPending || vaults.isPending || !password || !repeats(typed, me.username)} className={primaryButtonClass}>
              Delete my account
            </button>
          </form>
        </>
      )}
    </Section>
  );
}
