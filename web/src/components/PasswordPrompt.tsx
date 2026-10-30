// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { usePasswordPrompt } from "../state/passwordPrompt";
import ConfirmTyped from "./ConfirmTyped";

/** The one dialog for a remembered session's closed password window; the guarded request waits behind it. */
export default function PasswordPrompt() {
  const open = usePasswordPrompt((s) => s.open);
  // Unmounted on close, so the password never outlives the dialog.
  return open ? <Prompt /> : null;
}

function Prompt() {
  const settle = usePasswordPrompt((s) => s.settle);
  const [password, setPassword] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <ConfirmTyped
      title="Confirm your password"
      text="It's a while since you last gave your password on this device. Give it again to go on."
      action="Confirm"
      pending={pending}
      error={error}
      password={password}
      onPasswordChange={setPassword}
      onConfirm={async () => {
        setPending(true);
        setError(null);
        try {
          await call(api.POST("/api/session/confirm", { body: { password } }));
          settle(true);
        } catch (e) {
          // A wrong password is a 400: a 401 is the session itself gone, and the app is on its way to sign-in.
          if (e instanceof HttpError && e.status === 401) return settle(false);
          setError(errorText(e));
          setPending(false);
        }
      }}
      onClose={() => settle(false)}
    />
  );
}
