// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { useLocation } from "react-router-dom";
import { api, call, HttpError } from "../../api/client";
import { errorText } from "../../api/error-text";
import { useVaults } from "../../api/notes";
import { FormError, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { PasswordField } from "../../components/PasswordField";
import { RecoveryKeyReveal } from "../../components/RecoveryKeyReveal";
import { useMarkDiscreet } from "../../discreet/api";
import SetPinDialog from "../../discreet/SetPinDialog";
import { useDiscreet } from "../../discreet/state";
import { markPasswordChanged } from "../../offline/PasswordChangedCopies";
import { useSession } from "../../state/session";
import { Done, Section } from "./Section";

export default function Security() {
  return (
    <>
      <PasswordSection />
      <DiscreetSection />
      <RecoverySection />
      <ReencryptSection />
      <SessionsSection />
    </>
  );
}

function PasswordSection() {
  const revokeId = useId();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  // On by default: a key made from a stolen session shouldn't outlive the password change.
  const [revokeApiKeys, setRevokeApiKeys] = useState(true);
  const [changed, setChanged] = useState(false);
  const change = useMutation({
    mutationFn: () => call(api.POST("/api/password", { body: { oldPassword: current, newPassword: next, revokeApiKeys } })),
    onSuccess: ({ signedOut }) => {
      setCurrent("");
      setNext("");
      // Copies on other devices still open with the old password: the next visit lists them.
      markPasswordChanged();
      // The server ends every session, this one too: off to sign-in, the way back included.
      if (signedOut) useSession.getState().end("Your password is changed and every session has ended. Sign in with the new one.");
      else setChanged(true);
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    change.mutate();
  };

  return (
    <Section title="Password">
      <p className="text-text-body">Changing it ends every session, on your other devices and here: you sign in again with the new one.</p>
      <p className="text-text-body">
        An offline copy on another device still opens with the old password until that device next signs in. It stays, so
        nothing it hasn't uploaded is lost; after the change you see those devices, and can remove a copy you don't trust.
      </p>
      <form onSubmit={submit} className="flex flex-col gap-4">
        <PasswordField label="Current password" value={current} onChange={setCurrent} autoComplete="current-password" />
        <PasswordField label="New password" value={next} onChange={setNext} autoComplete="new-password" policy />
        <div className="flex flex-col gap-1">
          <label htmlFor={revokeId} className="flex min-h-11 items-center gap-3 text-text">
            <input
              id={revokeId}
              type="checkbox"
              checked={revokeApiKeys}
              onChange={(e) => setRevokeApiKeys(e.target.checked)}
              aria-describedby={`${revokeId}-hint`}
              className="size-5 shrink-0 accent-accent"
            />
            Also revoke all API keys and WebDAV shares, and disconnect apps
          </label>
          <p id={`${revokeId}-hint`} className="pl-8 text-sm text-text-body">
            Agents using them will need new keys, drives mounted with a share stop working, and apps will need to reconnect.
          </p>
        </div>
        {change.isError && <FormError>{errorText(change.error)}</FormError>}
        {changed && <Done>Your password is changed. Your other sessions have ended.</Done>}
        <button type="submit" disabled={change.isPending || !current || !next} className={primaryButtonClass}>
          Change password
        </button>
      </form>
    </Section>
  );
}

/**
 * The same for everyone (discreet spec §3.1), so that it tells nobody whether a PIN or discreet vaults exist: the way
 * to set or change the PIN. Only while the discreet vaults are shown, a switch per vault marks it, the vault Skills
 * excepted.
 */
function DiscreetSection() {
  const [setting, setSetting] = useState(false);
  const [set, setSet] = useState(false);
  const revealed = useDiscreet((s) => s.revealed);
  const { hash } = useLocation();
  const section = useRef<HTMLDivElement>(null);
  // More vaults… links here (`/settings/security#discreet-vaults`), as the install hint does to its section.
  useEffect(() => {
    if (hash === "#discreet-vaults") section.current?.scrollIntoView?.({ block: "start" });
  }, [hash]);
  return (
    <div ref={section}>
      <Section title="Discreet vaults" id="discreet-vaults">
        <p className="text-text-body">
          A PIN of its own shows the vaults you marked as discreet, from More vaults… in the vault menu, for the time you choose. Agents, synced devices,
          keys, WebDAV and connections see them as before.
        </p>
        {revealed && <DiscreetSwitches />}
        {set && <Done>The PIN is set.</Done>}
        <button
          type="button"
          onClick={() => {
            setSet(false);
            setSetting(true);
          }}
          className={secondaryButtonClass}
        >
          Set or change PIN
        </button>
        {setting && (
          <SetPinDialog
            onDone={() => {
              setSetting(false);
              setSet(true);
            }}
            onClose={() => setSetting(false)}
          />
        )}
      </Section>
    </div>
  );
}

/** A switch per vault, while the mode is on; a vault marked now stays shown until it ends. */
function DiscreetSwitches() {
  const vaults = useVaults();
  const mark = useMarkDiscreet();
  const listed = vaults.data?.filter((v) => v.kind !== "skills") ?? [];
  return (
    <>
      <p className="text-text-body">Marked vaults stay shown until discreet mode ends.</p>
      {listed.length > 0 && (
        <ul aria-label="Discreet vaults" className="flex flex-col gap-1">
          {listed.map((vault) => (
            <li key={vault.name}>
              <label className="flex min-h-11 items-center gap-3 text-text">
                <span className="min-w-0 flex-1 break-words">{vault.name}</span>
                <span className="text-sm text-muted" aria-hidden>
                  Discreet
                </span>
                <input
                  type="checkbox"
                  role="switch"
                  aria-label={`Discreet: ${vault.name}`}
                  checked={!!vault.discreet}
                  disabled={mark.isPending}
                  onChange={(e) => mark.mutate({ vault: vault.name, discreet: e.target.checked })}
                  className="size-5 shrink-0 accent-discreet"
                />
              </label>
            </li>
          ))}
        </ul>
      )}
      {mark.isError && <FormError>{errorText(mark.error)}</FormError>}
    </>
  );
}

function RecoverySection() {
  const [password, setPassword] = useState("");
  // The new key lives here, in component state only, until the reveal is done.
  const [recoveryKey, setRecoveryKey] = useState<string | null>(null);
  const [replaced, setReplaced] = useState(false);
  const regenerate = useMutation({
    mutationFn: () => call(api.POST("/api/recovery-key", { body: { currentPassword: password } })),
    // The result carries the recovery key: it goes into component state, not the client-wide mutation cache.
    gcTime: 0,
    onSuccess: (view) => {
      setPassword("");
      setReplaced(false);
      setRecoveryKey(view.recoveryKey);
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    regenerate.mutate();
  };

  return (
    <Section title="Recovery key">
      {recoveryKey ? (
        <RecoveryKeyReveal
          value={recoveryKey}
          onContinue={() => {
            regenerate.reset();
            setRecoveryKey(null);
            setReplaced(true);
          }}
        />
      ) : (
        <>
          <p className="text-text-body">
            A new recovery key replaces your current one: the old one stops working at once. It is shown only once.
          </p>
          <form onSubmit={submit} className="flex flex-col gap-4">
            <PasswordField label="Current password" value={password} onChange={setPassword} autoComplete="current-password" />
            {regenerate.isError && <FormError>{errorText(regenerate.error)}</FormError>}
            {replaced && <Done>Your new recovery key is in place; the old one no longer works.</Done>}
            <button type="submit" disabled={regenerate.isPending || !password} className={secondaryButtonClass}>
              Make a new recovery key
            </button>
          </form>
        </>
      )}
    </Section>
  );
}

/**
 * New keys for the account and every vault. The server rotates the user key and revokes every API key, app and WebDAV
 * share before it answers; the vaults are re-encrypted in the background, each showing as such in the vault list until it is done.
 */
function ReencryptSection() {
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  // As for a new recovery key: the key lives in component state only, until the reveal is done.
  const [recoveryKey, setRecoveryKey] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const reencrypt = useMutation({
    mutationFn: () => call(api.POST("/api/account/reencrypt", { body: { password } })),
    // The result carries the new recovery key: never in the client-wide mutation cache.
    gcTime: 0,
    onSuccess: (view) => {
      setPassword("");
      setDone(false);
      setRecoveryKey(view.recoveryKey);
      void Promise.all(["vaults", "keys", "connections", "dav-shares"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    reencrypt.mutate();
  };

  return (
    <Section title="Re-encrypt my vaults">
      {recoveryKey ? (
        <RecoveryKeyReveal
          value={recoveryKey}
          onContinue={() => {
            reencrypt.reset();
            setRecoveryKey(null);
            setDone(true);
          }}
        />
      ) : (
        <>
          <p className="text-text-body">
            New keys for your account and every vault. API keys, connected apps and WebDAV shares lose access and need to be set up or
            reconnected; you get a new recovery key. If you think your password leaked, change it too.
          </p>
          <form onSubmit={submit} className="flex flex-col gap-4">
            <PasswordField label="Current password" value={password} onChange={setPassword} autoComplete="current-password" />
            {reencrypt.isError && (
              <FormError>
                {errorText(reencrypt.error)}
                {/* A refusal (wrong password, a conflict, too many tries) changed nothing. A fault or a lost connection
                    may have come after the new keys were in place, with the new recovery key lost on the way: the old
                    one no longer works then. */}
                {!(reencrypt.error instanceof HttpError && reencrypt.error.status < 500) &&
                  ' The re-encryption may have gone through even so: make a new recovery key under "Recovery key" above, since the old one may no longer work.'}
              </FormError>
            )}
            {done && <Done>Your vaults are being re-encrypted with new keys, and your new recovery key is in place. The vault list shows each until it is done.</Done>}
            <button type="submit" disabled={reencrypt.isPending || !password} className={secondaryButtonClass}>
              {reencrypt.isPending ? "Re-encrypting…" : "Re-encrypt my vaults"}
            </button>
          </form>
        </>
      )}
    </Section>
  );
}

function SessionsSection() {
  const endOthers = useMutation({ mutationFn: () => call(api.POST("/api/sessions/end-others")) });
  return (
    <Section title="Sessions">
      <p className="text-text-body">Signs you out everywhere else, such as a browser you forgot to sign out of. This one stays.</p>
      {endOthers.isError && <FormError>{errorText(endOthers.error)}</FormError>}
      {endOthers.isSuccess && (
        <Done>Every other session has ended. Every other device's offline copy stays until you remove it in Settings, Offline.</Done>
      )}
      <button type="button" disabled={endOthers.isPending} onClick={() => endOthers.mutate()} className={secondaryButtonClass}>
        Sign out other sessions
      </button>
    </Section>
  );
}
