// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { AuthScreen, FormError, primaryButtonClass, TextField } from "../components/form";
import { PasswordField } from "../components/PasswordField";

export default function ForgotPassword() {
  const [username, setUsername] = useState("");
  const [recoveryKey, setRecoveryKey] = useState("");
  const [newPassword, setNewPassword] = useState("");

  const recover = useMutation({
    mutationFn: () => call(api.POST("/api/recover", { body: { username, recoveryKey, newPassword } })),
    // The key was typed in once; it doesn't stay in memory longer than needed.
    onSuccess: () => setRecoveryKey(""),
  });

  if (recover.isSuccess) {
    return (
      <AuthScreen title="Password set" lead="Your new password is set. Every session of yours has ended, here and on other devices.">
        <Link to="/signin" className={`${primaryButtonClass} flex items-center justify-center`}>
          Sign in
        </Link>
      </AuthScreen>
    );
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    recover.mutate();
  }

  return (
    <AuthScreen title="Forgot password" lead="With the recovery key you saved when you joined, you can set a new password.">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <TextField label="Username" value={username} onChange={setUsername} autoComplete="username" />
        <TextField label="Recovery key" value={recoveryKey} onChange={setRecoveryKey} autoComplete="off" mono />
        <PasswordField label="New password" value={newPassword} onChange={setNewPassword} autoComplete="new-password" policy />
        {recover.isError && <FormError>{errorText(recover.error)}</FormError>}
        <button type="submit" disabled={recover.isPending} className={primaryButtonClass}>
          Set new password
        </button>
      </form>
      <Link to="/signin" className="flex min-h-11 items-center self-start text-accent-text underline-offset-4 hover:underline">
        Back to sign-in
      </Link>
    </AuthScreen>
  );
}
