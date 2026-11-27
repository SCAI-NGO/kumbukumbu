// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import type { Conflict } from "eidolon-core/mode/mode";
import { CloudOff } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import { noteUrl } from "../api/paths";
import ConfirmTyped from "../components/ConfirmTyped";
import { BarButton, useTimeout } from "../note/ChangedElsewhere";
import { useSession } from "../state/session";
import { useShellNavigate } from "../windows/context";
import { useOfflineCopy } from "./copy";
import { contact, useOfflineMode } from "./mode";
import { endPendingSignOut } from "./unlock";

export const OFFLINE_SETTINGS = "/settings/offline";

const barClass = "flex min-h-11 flex-none flex-wrap items-center gap-x-1 border-b border-line bg-surface pr-1.5 pl-4 text-[13px] text-text";
const Dot = () => <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />;

const changes = (n: number) => (n === 1 ? "1 change" : `${n} changes`);

/** The bar's sentence while the app is in offline mode (offline mode design §3a, "The bar and the mark"). */
export function offlineSentence(phase: "offline" | "uploading" | "signIn", waiting: number, hasCopy: boolean): string {
  if (phase === "signIn") return `Sign in to upload your ${changes(waiting)}`;
  if (phase === "uploading") return waiting > 0 ? `Back online. Uploading ${changes(waiting)}…` : "Back online.";
  if (!hasCopy) return "You're offline. This device keeps no copy of your notes.";
  if (waiting > 0) return `You're offline. ${changes(waiting)} will upload when you're back.`;
  return "You're offline. Your notes are here; what needs a connection is marked.";
}

/** The note's name as the tree shows it: its file name without ".md". */
const noteName = (path: string) => path.slice(path.lastIndexOf("/") + 1).replace(/\.md$/, "");

/** What one came-back note says: why a note the person or someone else trashed is there again. */
function cameBackSentence(c: Conflict): string {
  return c.came === "trashed-here"
    ? `“${noteName(c.copy)}” came back: it was changed elsewhere after you trashed it.`
    : `“${noteName(c.copy)}” was trashed elsewhere; it's back with your changes.`;
}

/** The conflicts line: it stays until opened or dismissed, unlike the done line. `only`: the one conflict, if one. */
export function conflictSentence(uploaded: number | null, conflicts: number, only?: Conflict): string {
  const what = conflicts !== 1 ? `${conflicts} notes were also changed elsewhere.`
    : only?.came !== undefined ? cameBackSentence(only)
    : "One note was also changed elsewhere: your version is kept beside it.";
  return uploaded === null ? what : `Uploaded ${changes(uploaded)}. ${what}`;
}

/**
 * The offline mode's line, in the page's flow under the top bar like the update bar: always there while offline and
 * never dismissable; it leads to Settings, Offline. After it, the done line for four seconds, the conflicts until
 * seen, and a purge's notice. The conflicts name notes: only the signed-in shell (`inShell`) lists them, never the
 * sign-in page or another page outside it.
 */
export default function OfflineBar({ inShell = false }: { inShell?: boolean }) {
  const offline = useOfflineMode((s) => s.offline);
  const phase = useOfflineMode((s) => s.phase);
  const waiting = useOfflineMode((s) => s.waiting);
  const done = useOfflineMode((s) => s.done);
  const conflicts = useOfflineMode((s) => s.conflicts);
  const purged = useOfflineMode((s) => s.purged);
  const hasCopy = useOfflineCopy((s) => s.header !== null || s.stored);
  const [signingIn, setSigningIn] = useState(false);
  useTimeout(!offline && done !== null && conflicts.length === 0, 4_000, () => useOfflineMode.getState().seen());

  if (offline && phase !== "online") {
    return (
      <div role="status" aria-label="Offline" className={barClass}>
        <Dot />
        <Link to={OFFLINE_SETTINGS} className="min-w-0 flex-1 py-2 pl-1.5 underline-offset-4 hover:underline">
          {offlineSentence(phase, waiting, hasCopy)}
        </Link>
        {phase === "signIn" && <BarButton onClick={() => setSigningIn(true)}>Sign in</BarButton>}
        {signingIn && <SignInAgain onClose={() => setSigningIn(false)} />}
      </div>
    );
  }

  if (inShell && conflicts.length > 0) return <ConflictsLine uploaded={done?.uploaded ?? null} />;

  if (done !== null) {
    return (
      <p role="status" aria-label="Uploaded" className={`${barClass} py-2`}>
        <Dot />
        <span className="pl-1.5">All changes uploaded</span>
      </p>
    );
  }

  if (purged !== null) {
    return (
      <div role="status" aria-label="Offline copy" className={barClass}>
        <Dot />
        <span className="min-w-0 flex-1 py-2 pl-1.5">
          {`A vault's copy was removed from this device: it's no longer available here${purged.lost > 0 ? `, with ${changes(purged.lost)} that hadn't uploaded` : ""}.`}
        </span>
        <BarButton onClick={() => useOfflineMode.getState().dismissPurged()}>Dismiss</BarButton>
      </div>
    );
  }
  return null;
}

/** Conflict copies the uploads made: Open goes to the copy, or lists them when there are several. */
function ConflictsLine({ uploaded }: { uploaded: number | null }) {
  const conflicts = useOfflineMode((s) => s.conflicts);
  const [listing, setListing] = useState(false);
  const vaults = useVaults();
  const navigate = useShellNavigate();
  const open = (c: (typeof conflicts)[number]) => {
    const name = vaults.data?.find((v) => v.id === c.vault)?.name;
    if (name !== undefined) navigate(noteUrl(`/${name}/${c.copy}`));
    useOfflineMode.getState().seen();
  };
  return (
    <div role="status" aria-label="Changed elsewhere" className={barClass}>
      <Dot />
      <span className="min-w-0 flex-1 py-2 pl-1.5">{conflictSentence(uploaded, conflicts.length, conflicts[0])}</span>
      <BarButton onClick={() => (conflicts.length === 1 ? open(conflicts[0]!) : setListing(!listing))}>Open</BarButton>
      <BarButton onClick={() => useOfflineMode.getState().seen()}>Dismiss</BarButton>
      {listing && (
        <ul className="basis-full pb-2 pl-3.5">
          {conflicts.map((c) => (
            <li key={`${c.vault}\n${c.copy}`}>
              <button type="button" onClick={() => open(c)} className="min-h-11 text-left text-accent-text underline-offset-4 hover:underline">
                {c.copy}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * Sign in again over the app (§3a): the note, the place and the unlocked copy stay, and the upload starts at once.
 * The sign-in page's password field, the username known and given to password managers as a hidden field.
 */
function SignInAgain({ onClose }: { onClose(): void }) {
  const username = useSession((s) => s.user?.username ?? s.previousUser ?? "");
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <ConfirmTyped
      onTop
      title="Sign in again"
      text={`Your session has ended. Sign in as ${username} to upload your changes.`}
      action="Sign in"
      pending={pending}
      error={error}
      password={password}
      onPasswordChange={setPassword}
      onConfirm={async () => {
        setPending(true);
        setError(null);
        try {
          await endPendingSignOut();
          const view = await call(api.POST("/api/session", { body: { username, password, remember: false } }));
          queryClient.setQueryData(["session"], view);
          useSession.getState().signedIn(view);
          useOfflineMode.getState().signedIn();
          void contact();
          onClose();
        } catch (e) {
          setError(errorText(e));
          setPending(false);
        }
      }}
      onClose={onClose}
    >
      <input type="text" name="username" autoComplete="username" value={username} readOnly hidden />
    </ConfirmTyped>
  );
}

/** The sidebar's mark beside the vault name while offline: one tap to Settings, Offline. */
export function OfflineMark() {
  const offline = useOfflineMode((s) => s.offline);
  if (!offline) return null;
  return (
    <Link
      to={OFFLINE_SETTINGS}
      aria-label="Offline: settings"
      title="You're offline"
      className="flex size-11 flex-none items-center justify-center rounded-[10px] text-muted hover:text-text focus-visible:outline-2 focus-visible:outline-accent"
    >
      <CloudOff size={18} aria-hidden />
    </Link>
  );
}
