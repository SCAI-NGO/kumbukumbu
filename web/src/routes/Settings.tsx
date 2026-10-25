// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation } from "@tanstack/react-query";
import { useId, useState, type FormEvent, type ReactNode } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { AccountScreen, FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { RecoveryKeyReveal } from "../components/RecoveryKeyReveal";
import { useSession } from "../state/session";
import { applyScheme, storedScheme } from "../theme/apply";
import { schemes, type Scheme, type Tokens } from "../theme/schemes";

export default function Settings() {
  return (
    <AccountScreen title="Settings">
      <SchemeSection />
      <PasswordSection />
      <RecoverySection />
      <SessionsSection />
    </AccountScreen>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="flex flex-col gap-4 rounded-[14px] border border-line bg-surface p-4">
      <h2 id={id} className="text-lg font-bold text-text">
        {title}
      </h2>
      {children}
    </section>
  );
}

function Done({ children }: { children: ReactNode }) {
  return (
    <p role="status" className="text-text">
      {children}
    </p>
  );
}

const follow = "system";

/** Kept in this browser only, and only the scheme's name (there is no account setting for it). */
function SchemeSection() {
  const [chosen, setChosen] = useState(() => storedScheme() ?? follow);

  const choose = (id: string) => {
    setChosen(id);
    applyScheme(id === follow ? null : id);
  };

  return (
    <Section title="Colour scheme">
      <div role="radiogroup" aria-label="Colour scheme" className="flex flex-col gap-4">
        <SchemeOption id={follow} name="Follow the system" chosen={chosen} onChoose={choose} />
        <SchemeGroup title="Dark" list={schemes.filter((s) => !s.light)} chosen={chosen} onChoose={choose} />
        <SchemeGroup title="Light" list={schemes.filter((s) => s.light)} chosen={chosen} onChoose={choose} />
      </div>
    </Section>
  );
}

function SchemeGroup({ title, list, chosen, onChoose }: { title: string; list: readonly Scheme[]; chosen: string; onChoose: (id: string) => void }) {
  return (
    <div role="group" aria-label={title} className="flex flex-col">
      <h3 className="font-mono text-xs uppercase tracking-[0.08em] text-muted">{title}</h3>
      {list.map((scheme) => (
        <SchemeOption key={scheme.id} id={scheme.id} name={scheme.name} tokens={scheme.tokens} chosen={chosen} onChoose={onChoose} />
      ))}
    </div>
  );
}

function SchemeOption({ id, name, tokens, chosen, onChoose }: { id: string; name: string; tokens?: Tokens; chosen: string; onChoose: (id: string) => void }) {
  return (
    <label className="flex min-h-11 cursor-pointer items-center gap-3 text-text">
      <input type="radio" name="scheme" value={id} checked={chosen === id} onChange={() => onChoose(id)} className="size-5 shrink-0 accent-accent" />
      {name}
      {tokens && (
        // The scheme's own ground, surface and accent, so the choice is visible before it is made.
        <span aria-hidden className="ml-auto flex h-6 w-14 shrink-0 overflow-hidden rounded border border-line" data-testid={`swatch-${id}`}>
          <span className="flex-1" style={{ background: tokens.bg }} />
          <span className="flex-1" style={{ background: tokens.surface2 }} />
          <span className="flex-1" style={{ background: tokens.accent }} />
        </span>
      )}
    </label>
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
            Also revoke all API keys
          </label>
          <p id={`${revokeId}-hint`} className="pl-8 text-sm text-text-body">
            Agents using them will need new keys.
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

function SessionsSection() {
  const endOthers = useMutation({ mutationFn: () => call(api.POST("/api/sessions/end-others")) });
  return (
    <Section title="Sessions">
      <p className="text-text-body">Signs you out everywhere else, such as a browser you forgot to sign out of. This one stays.</p>
      {endOthers.isError && <FormError>{errorText(endOthers.error)}</FormError>}
      {endOthers.isSuccess && <Done>Every other session has ended.</Done>}
      <button type="button" disabled={endOthers.isPending} onClick={() => endOthers.mutate()} className={secondaryButtonClass}>
        Sign out other sessions
      </button>
    </Section>
  );
}
