// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState, type ReactNode } from "react";
import type { components } from "../api/schema";
import { labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { CopyButton, LeaveDialog, useLeaveGuard } from "../components/shownOnce";

type DavShare = components["schemas"]["DavShareView"];

const codeClass = "rounded-[6px] bg-surface-2 px-1 font-mono text-[0.9em] break-all text-text";
const blockClass = "rounded-[12px] border border-line bg-surface p-4 font-mono text-sm leading-relaxed break-all whitespace-pre-wrap text-text-body select-all";

const Code = ({ children }: { children: ReactNode }) => <code className={codeClass}>{children}</code>;

/** A value with its own copy button: the URL, the username and, once, the password. */
function Field({ label, copy, value, onCopied }: { label: string; copy: string; value: string; onCopied(ok: boolean): void }) {
  return (
    <div className="flex flex-col gap-1.5">
      <p className={labelClass}>{label}</p>
      <pre aria-label={label} className={`${blockClass} text-base text-text`}>
        {value}
      </pre>
      <CopyButton value={value} label={copy} className={secondaryButtonClass} onResult={onCopied} />
    </div>
  );
}

/** One client's steps for `username` on `host` (WebDAV design §3.9); none of them holds the password. */
function clients(username: string, host: string): { name: string; steps: ReactNode }[] {
  const url = `https://${host}/dav/`;
  const dolphin = `webdavs://${username}@${host}/dav/`;
  const rclone = `[kumbu]\ntype = webdav\nurl = ${url}\nvendor = other\nuser = ${username}\npass = <the output of rclone obscure>`;
  return [
    {
      name: "KDE Dolphin",
      steps: (
        <p>
          Open{" "}
          <a href={dolphin} className="font-mono break-all text-accent underline">
            {dolphin}
          </a>{" "}
          in Dolphin, or type it into its location bar. Dolphin asks for the password and can keep it in KWallet.
        </p>
      ),
    },
    {
      name: "GNOME Files",
      steps: (
        <p>
          Other Locations → Connect to Server: <Code>{`davs://${username}@${host}/dav/`}</Code>, then the password.
        </p>
      ),
    },
    {
      name: "macOS Finder",
      steps: (
        <p>
          Go → Connect to Server (⌘K) → <Code>{url}</Code>, then connect as a registered user with the name <Code>{username}</Code> and the
          password.
        </p>
      ),
    },
    {
      name: "Windows Explorer",
      steps: (
        <>
          <p>
            This PC → Map network drive, folder <Code>{url}</Code>, with “Connect using different credentials”: the user name{" "}
            <Code>{username}</Code> and the password.
          </p>
          <p>Or in a command prompt, which asks for the password:</p>
          <pre className={blockClass}>{`net use K: ${url} /user:${username} *`}</pre>
        </>
      ),
    },
    {
      name: "rclone",
      steps: (
        <>
          <p>
            Add this to rclone's config file (<Code>rclone config file</Code> says where it is):
          </p>
          <pre className={blockClass}>{rclone}</pre>
          <p>
            rclone keeps the password obscured: put the output of <Code>rclone obscure -</Code>, which reads the password from its input
            rather than the command line, after <Code>pass =</Code>. Then <Code>rclone ls kumbu:</Code> lists your vaults.
          </p>
        </>
      ),
    },
    {
      name: "Obsidian Remotely Save",
      steps: (
        <p>
          Another way to sync a vault with Obsidian, for instance on iOS. In the plugin's settings, choose WebDAV as the remote service and
          enter the server address <Code>{url}</Code>, the username <Code>{username}</Code> and the password.
        </p>
      ),
    },
    {
      name: "Nextcloud External storage",
      steps: (
        <>
          <p>
            In your personal Nextcloud settings, External storage: backend WebDAV, host <Code>{host}</Code>, remote subfolder{" "}
            <Code>/dav/</Code>, secure (https) on, the username <Code>{username}</Code> and the password.
          </p>
          <p>
            Make one share for each Nextcloud user who mounts it, as their personal external storage. Never one admin mount for everyone:
            every Nextcloud user would reach your vaults as you.
          </p>
        </>
      ),
    },
  ];
}

/**
 * How to mount a WebDAV share: its URL and username, each with a copy button, and each client's steps with the
 * username filled in. Right after the share is made, also its password, shown this once and kept in component state
 * only; until something was copied, leaving asks once. From a share's row it comes without the password, which the
 * app no longer has.
 */
export default function DavConnect({ share, password, onDone }: { share: DavShare; password?: string; onDone(): void }) {
  const id = useId();
  const [saved, setSaved] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const [closing, setClosing] = useState(false);
  const shownOnce = password !== undefined;
  const blocker = useLeaveGuard(saved || !shownOnce);
  const host = window.location.host;

  // Only the password counts as saved: the URL and the username can be read here again.
  const copied = (ok: boolean, secret = false) => {
    setCopyFailed(!ok);
    if (ok && secret) setSaved(true);
  };

  const leave = {
    id: `${id}-leave`,
    title: "You haven't saved the password",
    text: "You won't see this password again. Without it, you'll need to make a new share.",
  };

  return (
    <section aria-labelledby={`${id}-title`} className="flex min-w-0 flex-col gap-5">
      <h2 id={`${id}-title`} className="text-[22px] font-bold break-words text-text">
        Connect “{share.label}”
      </h2>
      {shownOnce ? (
        <p className="text-text-body">
          The password is shown only now. Put the URL, the username and the password into your password manager; your file manager can then
          mount the share.
        </p>
      ) : (
        <p className="text-text-body">The password was shown once, when the share was made; it is in your password manager if you saved it there.</p>
      )}

      <Field label="URL" copy="Copy URL" value={`https://${host}/dav/`} onCopied={(ok) => copied(ok)} />
      <Field label="Username" copy="Copy username" value={share.username} onCopied={(ok) => copied(ok)} />
      {shownOnce && <Field label="Password" copy="Copy password" value={password} onCopied={(ok) => copied(ok, true)} />}

      {copyFailed && (
        <p aria-live="polite" className="text-sm text-text">
          Copying didn't work. Select the text and copy it by hand.
        </p>
      )}

      <section aria-labelledby={`${id}-how`} className="flex flex-col gap-2">
        <h3 id={`${id}-how`} className="text-lg font-bold text-text">
          How to connect
        </h3>
        {clients(share.username, host).map((client) => (
          <details key={client.name} className="rounded-[12px] border border-line bg-surface px-4 py-3">
            <summary className="min-h-11 cursor-pointer content-center font-medium text-text">{client.name}</summary>
            <div className="flex flex-col gap-2 pt-2 text-text-body">{client.steps}</div>
          </details>
        ))}
      </section>

      {shownOnce ? (
        <button type="button" onClick={() => (saved ? onDone() : setClosing(true))} className={primaryButtonClass}>
          Done
        </button>
      ) : (
        <button type="button" onClick={onDone} className={secondaryButtonClass}>
          Back to the shares
        </button>
      )}

      {blocker.state === "blocked" && <LeaveDialog {...leave} onStay={() => blocker.reset?.()} onLeave={() => blocker.proceed?.()} />}
      {closing && <LeaveDialog {...leave} onStay={() => setClosing(false)} onLeave={onDone} />}
    </section>
  );
}
