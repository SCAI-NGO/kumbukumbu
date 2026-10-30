// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { AuthScreen, FormError, primaryButtonClass, TextField } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";

/**
 * Where to go after signing in: `next` only if it is a path on this site. "//host" and "/\host" are
 * protocol-relative to browsers, so the second character must be neither slash nor backslash.
 */
export function safeNext(next: string | null): string {
  if (!next || !/^\/(?![/\\])/.test(next)) return "/";
  const url = new URL(next, window.location.origin);
  return url.origin === window.location.origin ? url.pathname + url.search + url.hash : "/";
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

  const signIn = useMutation({
    mutationFn: () => call(api.POST("/api/session", { body: { username, password, remember } })),
    onSuccess: (view) => {
      // Nothing cached from an earlier session (maybe another user's) outlives this sign-in.
      queryClient.clear();
      queryClient.setQueryData(["session"], view);
      let next = safeNext(params.get("next"));
      // Someone else than the user whose session ended here: their notes, unsaved text and paths (content too) go.
      const previous = useSession.getState().previousUser;
      if (previous && previous !== view.user.username) {
        useEditor.getState().reset();
        if (next.startsWith("/notes/")) next = "/";
      }
      signedIn(view);
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
      <form onSubmit={submit} className="flex flex-col gap-4">
        <TextField label="Username" value={username} onChange={setUsername} autoComplete="username" />
        <PasswordField label="Password" value={password} onChange={setPassword} autoComplete="current-password" />
        <label className="flex min-h-11 items-center gap-3 text-text">
          <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} className="size-5 accent-accent" />
          Keep me signed in on this device
        </label>
        {remember && (
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
