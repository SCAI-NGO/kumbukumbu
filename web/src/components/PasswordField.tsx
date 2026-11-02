// SPDX-License-Identifier: AGPL-3.0-or-later
import { Eye, EyeOff } from "lucide-react";
import { useId, useState } from "react";
import { useSession } from "../state/session";
import { inputClass, labelClass } from "./form";

/**
 * A password input with a show toggle. `policy` is for a new password: it adds the server's rule and the
 * warning that username and password are the weakest way in.
 */
export function PasswordField(props: {
  label: string;
  value: string;
  onChange(value: string): void;
  autoComplete: "current-password" | "new-password";
  policy?: boolean;
}) {
  const id = useId();
  const [shown, setShown] = useState(false);
  const username = useSession((s) => s.user?.username);
  // Managers fill the field before a password with the username. Give them one that is meant: hidden by clipping,
  // as they skip display: none. On the sign-in page nobody is signed in, and the real username field serves.
  const hiddenUsername = props.autoComplete === "current-password" && username !== undefined;
  return (
    <div className="flex flex-col gap-1.5">
      {hiddenUsername && (
        <input type="text" value={username} readOnly tabIndex={-1} aria-hidden="true" autoComplete="username" className="sr-only" />
      )}
      <label htmlFor={id} className={labelClass}>
        {props.label}
      </label>
      <div className="relative">
        <input
          id={id}
          type={shown ? "text" : "password"}
          value={props.value}
          onChange={(e) => props.onChange(e.target.value)}
          autoComplete={props.autoComplete}
          aria-describedby={props.policy ? `${id}-hint` : undefined}
          required
          className={`${inputClass} pr-12`}
        />
        <button
          type="button"
          onClick={() => setShown((s) => !s)}
          aria-label={shown ? "Hide password" : "Show password"}
          className="absolute top-0 right-0 flex size-12 items-center justify-center text-muted hover:text-text"
        >
          {shown ? <EyeOff size={18} aria-hidden /> : <Eye size={18} aria-hidden />}
        </button>
      </div>
      {props.policy && (
        <div id={`${id}-hint`} className="flex flex-col gap-1 text-sm">
          <p className="text-muted">At least 12 characters, and not one of the most common passwords.</p>
          <p className="text-muted">Username and password are less safe than GitHub with a passkey; those come later.</p>
        </div>
      )}
    </div>
  );
}
