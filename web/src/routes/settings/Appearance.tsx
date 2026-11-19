// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { api, call } from "../../api/client";
import { errorText } from "../../api/error-text";
import ConfirmTyped from "../../components/ConfirmTyped";
import { FormError } from "../../components/form";
import { usePreferences } from "../../attachments/downloadFiles";
import { setAutoRefresh, storedAutoRefresh } from "../../note/autoRefresh";
import { applyScheme, useStoredScheme } from "../../theme/apply";
import { schemes, type Scheme, type Tokens } from "../../theme/schemes";
import { isStandalone } from "../../update/InstallHint";
import { Section } from "./Section";

export default function Appearance() {
  return (
    <>
      <SchemeSection />
      <ElsewhereSection />
      <DownloadsSection />
      <InstallSection />
    </>
  );
}

const follow = "system";

/** Kept in this browser only, and only the scheme's name (there is no account setting for it). */
function SchemeSection() {
  // Read from the store, not kept here: the palette may choose one while this window is open.
  const chosen = useStoredScheme() ?? follow;
  const choose = (id: string) => applyScheme(id === follow ? null : id);

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

/**
 * Whether several files downloaded at once (the gallery's ZIP) need the password, as an export does. On by default;
 * turning it off takes the password (a stolen session alone can't), turning it back on none. One file never needs it.
 */
function DownloadsSection() {
  const id = useId();
  const queryClient = useQueryClient();
  const preferences = usePreferences();
  const [asking, setAsking] = useState(false);
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const set = useMutation({
    mutationFn: (ask: boolean) =>
      call(api.PUT("/api/account/preferences", { body: ask ? { askPasswordForDownloads: true } : { askPasswordForDownloads: false, password } })),
    onSuccess: (view) => {
      queryClient.setQueryData(["preferences"], view);
      setPassword("");
      setAsking(false);
    },
  });
  const on = preferences.data?.askPasswordForDownloads ?? true;
  const close = () => {
    set.reset();
    setPassword("");
    setAsking(false);
  };

  return (
    <Section title="Downloads">
      <label className="flex min-h-11 cursor-pointer items-start gap-3 text-text">
        <input
          type="checkbox"
          checked={on}
          disabled={!preferences.data || set.isPending}
          aria-describedby={`${id}-what`}
          onChange={(e) => (e.target.checked ? set.mutate(true) : setAsking(true))}
          className="mt-0.5 size-5 shrink-0 accent-accent"
        />
        Ask for my password when downloading several files
      </label>
      <p id={`${id}-what`} className="text-sm text-text-body">
        Several images or PDFs from the gallery come as one ZIP, outside Kumbu²'s encryption. A single file never asks.
      </p>
      {preferences.isError && <FormError>{errorText(preferences.error)}</FormError>}
      {set.isError && !asking && <FormError>{errorText(set.error)}</FormError>}
      {asking && (
        <ConfirmTyped
          title="Download several files without the password?"
          text="Anyone with this session could then take many files at once. Your password confirms it."
          action="Turn off"
          pending={set.isPending}
          error={set.isError ? errorText(set.error) : null}
          password={password}
          onPasswordChange={setPassword}
          onConfirm={() => set.mutate(false)}
          onClose={close}
        />
      )}
    </Section>
  );
}

/** Without a service worker there is no install banner: the browser's own menu installs it (spec §1). */
function InstallSection() {
  const { hash } = useLocation();
  const section = useRef<HTMLDivElement>(null);
  // The phone's hint links here (`/settings/appearance#install`); a navigation within the app doesn't scroll to it by itself.
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
