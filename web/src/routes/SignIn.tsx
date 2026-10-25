// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { AuthScreen, FormError, primaryButtonClass, TextField } from "../components/form";
import { PasswordField } from "../components/PasswordField";
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

  const signIn = useMutation({
    mutationFn: () => call(api.POST("/api/session", { body: { username, password } })),
    onSuccess: (view) => {
      // Nothing cached from an earlier session (maybe another user's) outlives this sign-in.
      queryClient.clear();
      queryClient.setQueryData(["session"], view);
      signedIn(view);
      navigate(safeNext(params.get("next")), { replace: true });
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
