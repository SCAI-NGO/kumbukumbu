// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import ConfirmTyped from "../components/ConfirmTyped";
import { AccountScreen, FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { useDialog } from "../hooks/useDialog";
import { RecoveryKeyReveal } from "../components/RecoveryKeyReveal";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { setAutoRefresh, storedAutoRefresh } from "../note/autoRefresh";
import { applyScheme, storedScheme } from "../theme/apply";
import { schemes, type Scheme, type Tokens } from "../theme/schemes";
import { isStandalone } from "../update/InstallHint";
import { useUpdate } from "../update/store";
import { appVersion, parseVersion } from "../update/version";

export default function Settings() {
  return (
    <AccountScreen title="Settings">
      <SchemeSection />
      <ElsewhereSection />
      <PasswordSection />
      <RecoverySection />
      <ReencryptSection />
      <SessionsSection />
      <VaultsSection />
      <DeleteAccountSection />
      <InstallSection />
      <AboutSection />
    </AccountScreen>
  );
}

function Section({ title, id: anchor, children }: { title: string; id?: string; children: ReactNode }) {
  const id = useId();
  return (
    <section id={anchor} aria-labelledby={id} className="flex flex-col gap-4 rounded-[14px] border border-line bg-surface p-4">
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

/** Kept in this browser only, like the scheme: Write mode is never refreshed by itself, whatever is chosen here. */
function ElsewhereSection() {
  const id = useId();
  const [on, setOn] = useState(storedAutoRefresh);
  return (
    <Section title="Changes made elsewhere">
      <label className="flex min-h-11 cursor-pointer items-start gap-3 text-text">
        <input
          type="checkbox"
          checked={on}
          aria-describedby={`${id}-what`}
          onChange={(e) => {
            setOn(e.target.checked);
            setAutoRefresh(e.target.checked);
          }}
          className="mt-0.5 size-5 shrink-0 accent-accent"
        />
        Refresh Read mode automatically
      </label>
      <p id={`${id}-what`} className="text-sm text-text-body">
        When the note you are reading is changed by an agent, another tab or another device, it is shown at once, with what changed tinted. Off, a bar
        says so and you refresh when you are ready. Write mode never replaces your text by itself. Kept on this device only.
      </p>
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
            Also revoke all API keys and disconnect apps
          </label>
          <p id={`${revokeId}-hint`} className="pl-8 text-sm text-text-body">
            Agents using them will need new keys, and apps will need to reconnect.
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

/**
 * New keys for the account and every vault. The server rotates the user key and revokes every API key and app before
 * it answers; the vaults are re-encrypted in the background, each showing as such in the vault list until it is done.
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
      void Promise.all(["vaults", "keys", "connections"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
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
            New keys for your account and every vault. API keys and connected apps lose access and need to be set up or reconnected; you get a new
            recovery key. If you think your password leaked, change it too.
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

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** Deleting a vault, with its name typed: the one place in the app that removes notes beyond the trash. */
function VaultsSection() {
  const vaults = useVaults();
  const [deleting, setDeleting] = useState<string | null>(null);
  const [rekeying, setRekeying] = useState<string | null>(null);
  const [rekeyed, setRekeyed] = useState<string | null>(null);
  return (
    <Section title="Vaults">
      <p className="text-text-body">
        Deleting a vault removes it for good, with every note in it, their history, the trash and the checkpoints. API keys keep their other vaults; a key that had only this one is revoked.
      </p>
      <p className="text-text-body">
        Re-encrypting a vault gives it a new key. API keys and connected apps with access to it lose it and need to be set up or reconnected.
      </p>
      {vaults.isError && <FormError>{errorText(vaults.error)}</FormError>}
      {vaults.data?.length === 0 && <p className="text-muted">You have no vault yet.</p>}
      {vaults.data && vaults.data.length > 0 && (
        <ul className="flex flex-col gap-2">
          {vaults.data.map((vault) => (
            <li key={vault.name} className="flex items-center gap-3">
              <span className="min-w-0 flex-1 break-words text-text">{vault.name}</span>
              <span className="shrink-0 text-sm text-muted">{vault.pending ? "Being re-encrypted" : plural(Number(vault.notes), "note", "notes")}</span>
              <button
                type="button"
                onClick={() => {
                  setRekeyed(null);
                  setRekeying(vault.name);
                }}
                aria-label={`Re-encrypt ${vault.name}`}
                className={`${secondaryButtonClass} shrink-0`}
              >
                Re-encrypt
              </button>
              <button type="button" onClick={() => setDeleting(vault.name)} aria-label={`Delete ${vault.name}`} className={`${secondaryButtonClass} shrink-0`}>
                Delete
              </button>
            </li>
          ))}
        </ul>
      )}
      {rekeyed && <Done>{rekeyed}</Done>}
      {deleting !== null && <DeleteVaultDialog vault={deleting} onClose={() => setDeleting(null)} />}
      {rekeying !== null && <RekeyVaultDialog vault={rekeying} onDone={setRekeyed} onClose={() => setRekeying(null)} />}
    </Section>
  );
}

/** What loses access is listed first; only then is the password asked. The answer waits for the vault to be done. */
function RekeyVaultDialog({ vault, onDone, onClose }: { vault: string; onDone(message: string): void; onClose(): void }) {
  const id = useId();
  const queryClient = useQueryClient();
  const dialog = useDialog<HTMLFormElement>(onClose);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const access = useQuery({
    queryKey: ["vault-access", vault],
    queryFn: () => call(api.GET("/api/vaults/{vault}/access", { params: { path: { vault } } })),
    // Always as it is now: a key made a moment ago must be on the list.
    staleTime: 0,
    gcTime: 0,
  });
  const rekey = useMutation({
    mutationFn: () => call(api.POST("/api/vaults/{vault}/rekey", { params: { path: { vault } }, body: { password } })),
    onSuccess: async (view) => {
      setPassword("");
      const keys = Number(view.revokedApiKeys);
      const apps = Number(view.disconnectedApps);
      onDone(
        keys + apps === 0
          ? `${vault} is re-encrypted.`
          : `${vault} is re-encrypted. ${plural(keys, "API key was", "API keys were")} revoked and ${plural(apps, "app was", "apps were")} disconnected.`,
      );
      await Promise.all(["vaults", "keys", "connections"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
      onClose();
    },
  });
  const lists = access.data;
  const nothing = lists && lists.apiKeys.length === 0 && lists.connections.length === 0;

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <form
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-busy={rekey.isPending}
        onSubmit={(e) => {
          e.preventDefault();
          if (lists && password) rekey.mutate();
        }}
        className="flex max-h-full w-full max-w-sm flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Re-encrypt {vault}?
        </h2>
        <p className="text-text-body">The vault gets a new key. What has access to it now loses it and needs to be set up or reconnected.</p>
        {access.isPending && <p className="text-muted">Looking up what has access…</p>}
        {access.isError && <FormError>{errorText(access.error)}</FormError>}
        {nothing && <p className="text-text-body">No API key or connected app has access to it.</p>}
        {lists && lists.apiKeys.length > 0 && <AccessList title="API keys" names={lists.apiKeys} />}
        {lists && lists.connections.length > 0 && <AccessList title="Connected apps" names={lists.connections} />}
        {lists && <PasswordField label="Your password" value={password} onChange={setPassword} autoComplete="current-password" />}
        {rekey.isPending && <p className="text-muted">Re-encrypting; this can take a while for a large vault.</p>}
        {rekey.isError && <FormError>{errorText(rekey.error)}</FormError>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="submit" disabled={!lists || !password || rekey.isPending} className={`${primaryButtonClass} flex-1`}>
            Re-encrypt vault
          </button>
        </div>
      </form>
    </div>
  );
}

function AccessList({ title, names }: { title: string; names: string[] }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1">
      <h3 id={id} className={labelClass}>
        {title}
      </h3>
      <ul aria-labelledby={id} className="list-disc pl-5 text-text">
        {names.map((name) => (
          <li key={name} className="break-words">
            {name}
          </li>
        ))}
      </ul>
    </div>
  );
}

function DeleteVaultDialog({ vault, onClose }: { vault: string; onClose(): void }) {
  const queryClient = useQueryClient();
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const remove = useMutation({
    mutationFn: () => call(api.POST("/api/vaults/{vault}/delete", { params: { path: { vault } }, body: { password, confirm: vault } })),
    onSuccess: async () => {
      useEditor.getState().forgetVault(vault);
      // Nothing of the vault stays in the caches: its tree, and every note, link and history read from it.
      queryClient.removeQueries({ predicate: (q) => q.queryKey.some((k) => typeof k === "string" && (k === vault || k.startsWith(`/${vault}/`))) });
      await Promise.all(["vaults", "keys", "search", "changes"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
      onClose();
    },
  });
  return (
    <ConfirmTyped
      title={`Delete ${vault}?`}
      text="The vault goes for good, with every note in it, their history, the trash and the checkpoints. Nobody can bring it back. An API key that had only this vault is revoked."
      word={vault}
      action="Delete vault"
      pending={remove.isPending}
      error={remove.isError ? errorText(remove.error) : null}
      onConfirm={() => remove.mutate()}
      onClose={onClose}
      password={password}
      onPasswordChange={setPassword}
    />
  );
}

/** Needs the password and the username typed out, so neither a stolen session nor a slip deletes the account. */
function DeleteAccountSection() {
  const id = useId();
  const me = useSession((s) => s.user);
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [typed, setTyped] = useState("");
  const remove = useMutation({
    mutationFn: () => call(api.POST("/api/account/delete", { body: { password, confirm: typed } })),
    onSuccess: () => {
      setPassword("");
      queryClient.clear();
      useEditor.getState().reset();
      useSession.getState().end("Your account is deleted, with all your vaults and notes.");
    },
  });

  if (!me) return null;
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (password && typed === me.username) remove.mutate();
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
            <PasswordField label="Current password" value={password} onChange={setPassword} autoComplete="current-password" />
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-confirm`} className={labelClass}>
                Type {me.username} to confirm
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
            <button type="submit" disabled={remove.isPending || !password || typed !== me.username} className={primaryButtonClass}>
              Delete my account
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

/** Without a service worker there is no install banner: the browser's own menu installs it (spec §1). */
function InstallSection() {
  const { hash } = useLocation();
  const section = useRef<HTMLDivElement>(null);
  // The phone's hint links here (`/settings#install`); a navigation within the app doesn't scroll to it by itself.
  useEffect(() => {
    if (hash === "#install") section.current?.scrollIntoView?.({ block: "start" });
  }, [hash]);

  return (
    <div ref={section}>
      <Section title="Install as an app" id="install">
        {isStandalone() ? (
          <p className="text-text-body">Kumbu² is installed as an app on this device.</p>
        ) : (
          <>
            <p className="text-text-body">Kumbu² gets an icon of its own and opens in its own window. Updates still arrive by themselves.</p>
            <InstallSteps title="Android (Chrome)">
              <li>Open the menu ⋮.</li>
              <li>
                Tap <b>Install app</b> (or <b>Add to Home screen</b>).
              </li>
            </InstallSteps>
            <InstallSteps title="iPhone and iPad (Safari)">
              <li>
                Tap <b>Share</b>.
              </li>
              <li>
                Tap <b>Add to Home Screen</b>.
              </li>
            </InstallSteps>
            <InstallSteps title="On the desktop (Chrome or Edge)">
              <li>Click the install icon at the right of the address bar,</li>
              <li>
                or open the menu and choose <b>Install Kumbu²</b>.
              </li>
            </InstallSteps>
          </>
        )}
      </Section>
    </div>
  );
}

function InstallSteps({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <h3 className="font-medium text-text">{title}</h3>
      <ol className="list-decimal pl-5 text-text-body">{children}</ol>
    </div>
  );
}

/** This app's build, and the server's when it differs (an update not taken yet). */
function AboutSection() {
  const app = parseVersion(appVersion);
  const server = parseVersion(useUpdate((s) => s.server));
  const differs = app && server && (app.semver !== server.semver || app.sha !== server.sha);
  return (
    <Section title="About">
      <p className="text-text-body">{app ? `Kumbu² ${app.semver} (${app.sha})` : "Kumbu² development build"}</p>
      {differs && <p className="text-text-body">{`Server: ${server.semver} (${server.sha})`}</p>}
    </Section>
  );
}
