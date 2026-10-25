// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { components } from "../api/schema";
import { AuthScreen, FormError, primaryButtonClass, TextField } from "../components/form";
import { PasswordField } from "../components/PasswordField";
import { RecoveryKeyReveal } from "../components/RecoveryKeyReveal";
import { useSession } from "../state/session";

type Joined = components["schemas"]["JoinedView"];

const unusable: Record<string, string> = {
  used: "This invitation was already used. Ask for a new one.",
  expired: "This invitation has expired. Ask for a new one.",
  revoked: "This invitation was withdrawn. Ask for a new one.",
};

/**
 * The page of an invitation link, `/join#{code}`. The code sits in the fragment so it never reaches the server's
 * or a proxy's logs; it is read once, kept in state, sent only in request bodies, and cleared from the address bar.
 */
export default function Join() {
  const location = useLocation();
  const navigate = useNavigate();
  const [code] = useState(() => decodeURIComponent(location.hash.slice(1)));

  useEffect(() => {
    if (location.hash) navigate({ pathname: "/join" }, { replace: true });
  }, [location.hash, navigate]);

  const peek = useQuery({
    queryKey: ["invitation", code],
    queryFn: () => call(api.POST("/api/invitations/peek", { body: { code } })),
    enabled: code !== "",
    // Don't keep the code around in the cache once the page is gone.
    gcTime: 0,
  });

  if (!code) return <Unusable text="This link has no invitation in it. Open the full link you were sent." />;
  if (peek.isPending) return <AuthScreen title="Join Kumbu²" lead="Checking your invitation…" />;
  if (peek.isError) return <Unusable text={errorText(peek.error)} />;
  if (!peek.data.valid) return <Unusable text={unusable[peek.data.reason ?? ""] ?? "This invitation can't be used. Ask for a new one."} />;
  return <JoinForm code={code} role={peek.data.role} />;
}

function Unusable({ text }: { text: string }) {
  return <AuthScreen title="Join Kumbu²" lead={text} />;
}

function JoinForm({ code, role }: { code: string; role: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const signedIn = useSession((s) => s.signedIn);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [joined, setJoined] = useState<Joined | null>(null);

  const join = useMutation({
    mutationFn: () => call(api.POST("/api/join", { body: { code, username, password } })),
    onSuccess: setJoined,
  });

  if (joined) {
    return (
      <AuthScreen title="Your recovery key">
        <RecoveryKeyReveal
          value={joined.recoveryKey}
          username={joined.user.username}
          onContinue={() => {
            const view = { user: joined.user, previousSignInAt: null };
            queryClient.setQueryData(["session"], view);
            signedIn(view);
            navigate("/", { replace: true });
          }}
        />
      </AuthScreen>
    );
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    join.mutate();
  }

  return (
    <AuthScreen
      title="Join Kumbu²"
      lead={role === "user" ? "Choose a username and a password." : "You are invited as an admin. Choose a username and a password."}
    >
      <form onSubmit={submit} className="flex flex-col gap-4">
        <TextField label="Username" value={username} onChange={setUsername} autoComplete="username" />
        <PasswordField label="Password" value={password} onChange={setPassword} autoComplete="new-password" policy />
        {join.isError && <FormError>{errorText(join.error)}</FormError>}
        <button type="submit" disabled={join.isPending} className={primaryButtonClass}>
          Create account
        </button>
      </form>
    </AuthScreen>
  );
}
