// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useState, type FormEvent } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { useEmbeddingProviders } from "../api/notes";
import type { components } from "../api/schema";
import { AuthScreen, FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { timeText } from "../components/Sheet";
import { useEditor } from "../state/editor";
import { useSession, type SessionView } from "../state/session";
import { maintainSentence } from "./rights";

type Peek = components["schemas"]["VaultInvitationPeekView"];
type Accepted = components["schemas"]["AcceptedInvitationView"];

/**
 * Where the link's secret waits while its holder signs in: this tab's session storage, and nowhere else. Never in a
 * path, a query or the return address after sign-in, which would reach the server's and proxies' logs.
 */
export const shareSecretKey = "kumbu.shareSecret";

const roleText: Record<string, string> = { read: "Read", write: "Read and write", maintain: "Maintain" };

/** The secret after `#`, or "" for none or a garbled one (a cut-off escape such as `%E0`). */
function fragmentSecret(hash: string) {
  try {
    return decodeURIComponent(hash.slice(1));
  } catch {
    return "";
  }
}

/**
 * The session, or null without one. Asked past `api`, whose 401 would send the page to sign-in at once: someone
 * without an account first reads what to do (collab concept §4.3).
 */
async function sessionOrNull(): Promise<SessionView | null> {
  const response = await globalThis.fetch(new Request(`${window.location.origin}/api/session`, { credentials: "include" }));
  if (response.status === 401) return null;
  if (!response.ok) throw new HttpError(response.status, "");
  return (await response.json()) as SessionView;
}

/**
 * The page of a vault's invitation link, `/share#<secret>` (collab design §4.2): the secret is moved from the fragment
 * into this tab's session storage and the address becomes plain `/share`, also across signing in. Signed in, the
 * page shows what the invitation is (peek) and accepts it under the name the caller gives the vault; the secret is
 * forgotten only once that has worked, so a refusal (a name she has already) leaves the link usable.
 */
export default function Share() {
  const location = useLocation();
  const navigate = useNavigate();
  const [secret] = useState(() => {
    const fromLink = fragmentSecret(location.hash);
    if (fromLink) sessionStorage.setItem(shareSecretKey, fromLink);
    return fromLink || sessionStorage.getItem(shareSecretKey) || "";
  });

  useEffect(() => {
    if (location.hash) navigate({ pathname: "/share" }, { replace: true });
  }, [location.hash, navigate]);

  const signedIn = useSession((s) => s.signedIn);
  const session = useQuery({ queryKey: ["share-session"], queryFn: sessionOrNull, gcTime: 0 });
  useEffect(() => {
    if (session.data) signedIn(session.data);
  }, [session.data, signedIn]);

  if (!secret) return <AuthScreen title="A shared vault" lead="This link has no invitation in it. Open the full link you were sent." />;
  if (session.isPending) return <AuthScreen title="A shared vault" lead="Checking the invitation…" />;
  if (session.isError) return <AuthScreen title="A shared vault" lead={errorText(session.error)} />;
  if (!session.data) {
    return (
      <AuthScreen title="A shared vault" lead="Someone invited you to a vault of theirs on this Kumbu².">
        <p className="text-text-body">
          To accept, you need an account on this Kumbu² first. If you have none, tell the person who sent you the link that you haven't been invited yet:
          only an admin makes accounts here. The link stays valid until it expires, so you can open it again once you have one.
        </p>
        <Link to={`/signin?next=${encodeURIComponent("/share")}`} className={`${primaryButtonClass} flex items-center justify-center`}>
          Sign in
        </Link>
      </AuthScreen>
    );
  }
  return <Invitation secret={secret} />;
}

function Invitation({ secret }: { secret: string }) {
  const id = useId();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const providers = useEmbeddingProviders();
  // Vault names she has already, as the server said: the name must change before she can accept.
  const [taken, setTaken] = useState<string[]>([]);
  const [alias, setAlias] = useState<string | null>(null);
  const [accepted, setAccepted] = useState<Accepted | null>(null);

  const peek = useQuery({
    queryKey: ["vault-invitation"],
    queryFn: () => call(api.POST("/api/vault-invitations/peek", { body: { secret } })),
    // Nothing of it stays in the cache once the page is gone.
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
  });
  useEffect(() => {
    if (!peek.data || alias !== null) return;
    setAlias(peek.data.alias);
    if (!peek.data.aliasFree) setTaken([peek.data.alias.toLowerCase()]);
  }, [peek.data, alias]);

  const name = (alias ?? "").trim();
  const isTaken = taken.includes(name.toLowerCase());

  const accept = useMutation({
    mutationFn: async () => {
      try {
        return await call(api.POST("/api/vault-invitations/accept", { body: { secret, alias: name } }));
      } catch (e) {
        // A conflict may be the name, or something else (a membership she has already): peek tells which.
        if (e instanceof HttpError && e.status === 409) {
          const again = await call(api.POST("/api/vault-invitations/peek", { body: { secret, alias: name } }));
          if (!again.aliasFree) setTaken((t) => [...t, name.toLowerCase()]);
        }
        throw e;
      }
    },
    onSuccess: (view) => {
      // Only now: until it has worked, the link (and the secret here) must stay usable.
      sessionStorage.removeItem(shareSecretKey);
      setAccepted(view);
      void queryClient.invalidateQueries({ queryKey: ["vaults"] });
    },
  });

  if (accepted) {
    const open = () => {
      useEditor.getState().setActiveVault(accepted.vault);
      useEditor.getState().setOverlay("tree");
      navigate("/", { replace: true });
    };
    return accepted.status === "active" ? (
      <AuthScreen title={`${accepted.vault} is shared with you`} lead={`You can open it now, as ${roleText[accepted.role] ?? accepted.role}.`}>
        <button type="button" onClick={open} className={primaryButtonClass}>
          Open {accepted.vault}
        </button>
      </AuthScreen>
    ) : (
      <AuthScreen title="Accepted" lead={`The owner confirms your access first. ${accepted.vault} shows among your vaults once they have.`}>
        <button type="button" onClick={() => navigate("/", { replace: true })} className={secondaryButtonClass}>
          To your vaults
        </button>
      </AuthScreen>
    );
  }

  if (peek.isPending) return <AuthScreen title="A shared vault" lead="Checking the invitation…" />;
  if (peek.isError) return <AuthScreen title="A shared vault" lead={errorText(peek.error)} />;

  const p: Peek = peek.data;
  const provider = providers.data?.find((x) => x.name === p.embeddingProvider);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (name && !isTaken && !accept.isPending) accept.mutate();
  };
  const row = "flex flex-col gap-0.5";
  return (
    <AuthScreen title={`Join ${p.vault}`} lead="Someone shares a vault with you. Check whose it is before you accept.">
      <dl className="flex flex-col gap-3 rounded-[12px] border border-line bg-surface p-4">
        <div className={row}>
          <dt className={labelClass}>Vault</dt>
          <dd className="break-words text-text">{p.vault}</dd>
        </div>
        <div className={row}>
          <dt className={labelClass}>Owner</dt>
          <dd className="break-words text-text">{p.owner}</dd>
        </div>
        {/* Her own field: a display name is free text anyone could type; the username is hers alone. */}
        <div className={row}>
          <dt className={labelClass}>Owner's username</dt>
          <dd className="font-mono break-words text-text">{p.ownerUsername}</dd>
        </div>
        <div className={row}>
          <dt className={labelClass}>Your role</dt>
          <dd className={p.role === "maintain" ? "text-warning" : "text-text"}>
            {roleText[p.role] ?? p.role}
            {p.role === "maintain" && <span className="block text-sm">{maintainSentence}</span>}
          </dd>
        </div>
        <div className={row}>
          <dt className={labelClass}>Search by meaning</dt>
          <dd className="text-text">
            {provider?.label ?? p.embeddingProvider}
            {provider?.external && (
              <span className="block text-sm text-text-body">The vault's notes go to {provider.label}, and so do your searches that include it.</span>
            )}
          </dd>
        </div>
        <div className={row}>
          <dt className={labelClass}>The link works until</dt>
          <dd className="text-text">{timeText(p.expiresAt)}</dd>
        </div>
      </dl>
      {p.confirm && <p className="text-text-body">The owner confirms your access after you accept.</p>}
      <form onSubmit={submit} className="flex flex-col gap-4">
        <div className="flex flex-col gap-1.5">
          <label htmlFor={`${id}-alias`} className={labelClass}>
            Your name for this vault
          </label>
          <input
            id={`${id}-alias`}
            value={alias ?? ""}
            onChange={(e) => setAlias(e.target.value)}
            autoComplete="off"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            required
            aria-invalid={isTaken}
            aria-describedby={`${id}-alias-hint`}
            className={inputClass}
          />
          <p id={`${id}-alias-hint`} className={`text-sm ${isTaken ? "text-text" : "text-muted"}`}>
            {isTaken ? `You have a vault named ${name} already. Choose another name for this one.` : "Only you see it; the owner and others don't."}
          </p>
        </div>
        {accept.isError && !isTaken && <FormError>{errorText(accept.error)}</FormError>}
        <button type="submit" disabled={!name || isTaken || accept.isPending} className={primaryButtonClass}>
          Accept
        </button>
      </form>
    </AuthScreen>
  );
}
