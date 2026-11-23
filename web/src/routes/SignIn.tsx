// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { AuthScreen, FormError, primaryButtonClass, TextField } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { NEUTRAL } from "../discreet/neutralWindow";
import { openCopy, useOfflineMode } from "../offline/mode";
import { endPendingSignOut, sweepRemovedCopies, unlockOffline } from "../offline/unlock";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { rememberSignIn } from "../offline/turnOn";

/**
 * Where to go after signing in: `next` only if it is a path on this site. "//host" and "/\host" are
 * protocol-relative to browsers, so the second character must be neither slash nor backslash.
 */
export function safeNext(next: string | null): string {
  if (!next || !/^\/(?![/\\])/.test(next)) return "/";
  const url = new URL(next, window.location.origin);
  if (url.origin !== window.location.origin) return "/";
  // Dot segments and escapes can turn a harmless-looking input into "//host" once parsed, so the output is checked too.
  const result = url.pathname + url.search + url.hash;
  // A discreet view's address leads nowhere after a sign-in, which starts with the vaults hidden.
  if (url.pathname === NEUTRAL) return "/";
  return /^\/[/\\]/.test(result) ? "/" : result;
}

export default function SignIn() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const notice = useSession((s) => s.notice);
  const signedIn = useSession((s) => s.signedIn);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(false);

  // After a sign-out, nothing of the session stays cached. Cleared here, once the signed-in pages are gone: cleared
  // while they were still shown, they would ask again (for the session too) before the way here was in.
  useEffect(() => {
    if (useSession.getState().signedOut) queryClient.clear();
  }, [queryClient]);

  const offline = useOfflineMode((s) => s.offline);

  // A copy whose slot is gone goes from this device now, not only once its owner signs in here again.
  useEffect(() => {
    if (!offline) void sweepRemovedCopies();
  }, [offline]);

  /** Offline the form opens the copy on this device instead (offline mode design §3a): "offline" when it did. */
  const openHere = async (): Promise<"offline"> => {
    if (await unlockOffline(username, password)) return "offline";
    throw new HttpError(401, "The username or password is wrong, or this device keeps no notes of this user.");
  };

  const signIn = useMutation({
    mutationFn: async () => {
      if (offline) return openHere();
      // A sign-out made offline ends its session first: afterwards it would end this new one.
      await endPendingSignOut();
      try {
        return await call(api.POST("/api/session", { body: { username, password, remember } }));
      } catch (e) {
        // No answer at all: the copy on this device, if there is one.
        if (e instanceof TypeError) return openHere();
        throw e;
      }
    },
    onSuccess: (view) => {
      if (view === "offline") {
        // Who signed in is all this device knows offline; the session view comes once it is back online.
        signedIn({ user: { id: "", username, display: username, role: "user" }, previousSignInAt: null });
        navigate(safeNext(params.get("next")), { replace: true });
        return;
      }
      // Nothing cached from an earlier session (maybe another user's) outlives this sign-in.
      queryClient.clear();
      queryClient.setQueryData(["session"], view);
      let next = safeNext(params.get("next"));
      // Someone else than the user whose session ended here: their notes, unsaved text and paths (content too) go.
      const previous = useSession.getState().previousUser;
      if (previous && previous !== view.user.username) {
        useEditor.getState().reset();
        if (next.startsWith("/notes/") || next.startsWith("/n/")) next = "/";
      }
      // The offline copy's idle limit follows it (offline mode design §3a).
      rememberSignIn(remember);
      signedIn(view);
      // The contact at sign-in: the offline copy on this device opens, wrapped again under this password (design §4.2).
      void openCopy(view.user.id, { username, password, remember });
      navigate(next, { replace: true });
    },
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    signIn.mutate();
  }

  return (
    <AuthScreen title="Sign in">
      {notice && (
        <p role="status" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-text">
          {notice}
        </p>
      )}
      {offline && (
        <p role="status" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-text">
          You're offline. Signing in opens the notes kept on this device.
        </p>
      )}
      <form onSubmit={submit} className="flex flex-col gap-4">
        <TextField label="Username" value={username} onChange={setUsername} autoComplete="username" />
        <PasswordField label="Password" value={password} onChange={setPassword} autoComplete="current-password" />
        {/* Offline it would do nothing: the copy keeps the idle limit of the last sign-in online. */}
        {!offline && (
          <label className="flex min-h-11 items-center gap-3 text-text">
            <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} className="size-5 accent-accent" />
            Keep me signed in on this device
          </label>
        )}
        {remember && !offline && (
          <p role="note" className="rounded-[10px] bg-red-700 px-3 py-2 text-white">
            Comfortable, but less safe: anyone who gets into this device or browser gets into your notes until you sign out or 30 days are up. Use it only on a device of your own, with a screen lock.
          </p>
        )}
        {signIn.isError && <FormError>{errorText(signIn.error)}</FormError>}
        <button type="submit" disabled={signIn.isPending} className={primaryButtonClass}>
          Sign in
        </button>
      </form>
      <Link to="/forgot" className="flex min-h-11 items-center self-start text-accent-text underline-offset-4 hover:underline">
        Forgot your password?
      </Link>
    </AuthScreen>
  );
}
