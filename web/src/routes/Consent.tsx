// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useSearchParams } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { components } from "../api/schema";
import { AuthScreen, FormError, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useSession } from "../state/session";

type View = components["schemas"]["ConsentView"];

const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
// Big targets for the thumb: the same height as the primary button.
const bigSecondary = secondaryButtonClass.replace("min-h-11", "min-h-12");
const rightText = (right: string) => (right === "write" ? "read and write" : right);

/** What the server found wrong with the request, in words for the user. The few that cannot be answered by Allow. */
const problems: Record<string, string> = {
  not_a_connection: "This address isn't a connection. In Kumbu², open Access → Connect an app, and use the connector URL it shows.",
  other_user: "This connection belongs to another Kumbu² account. Sign out and sign in as its owner.",
  revoked: "This connection was revoked. Make a new connection in Kumbu² and add its URL in the app.",
  expired: "This connection expired. Make a new connection in Kumbu² and add its URL in the app.",
};

/** Errors found before the app's redirect address was trusted: nothing is sent back to the app, so no session is needed. */
export function ConsentError({ code }: { code: string }) {
  const text =
    code === "unknown_client"
      ? "The app could not be identified, so Kumbu² can't connect it."
      : code === "invalid_redirect_uri"
        ? "The app asked to return to an address it didn't register. Nothing was connected."
        : "The app's request could not be used. Nothing was connected.";
  return (
    <AuthScreen title="Nothing was connected">
      <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-text">
        {text}
      </p>
    </AuthScreen>
  );
}

/**
 * What an app asks of the user, with Allow and Deny and nothing else to choose (the connection was made in Access).
 * The decision's answer holds the authorisation code, so it is kept in no cache and no storage: the browser
 * leaves for the app as soon as it arrives.
 */
export default function Consent() {
  const [params] = useSearchParams();
  const id = params.get("request") ?? "";
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // gcTime 0: nothing about the request is kept once the page is left. A refusal is final; no retries.
  const request = useQuery({
    queryKey: ["consent", id],
    queryFn: () => call(api.GET("/api/oauth/requests/{id}", { params: { path: { id } } })),
    enabled: id !== "",
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const decide = useMutation({
    mutationFn: (allow: boolean) => call(api.POST("/api/oauth/requests/{id}/decision", { params: { path: { id } }, body: { allow } })),
    gcTime: 0,
    // A full navigation (not the router): the app's address is another site.
    onSuccess: ({ redirect }) => window.location.assign(redirect),
  });
  const busy = decide.isPending || decide.isSuccess;

  const signOut = useMutation({
    mutationFn: async () => {
      const { response } = await api.DELETE("/api/session");
      // A 401: the session had ended already, which is what was asked for.
      if (!response.ok && response.status !== 401) throw new Error("sign-out failed");
    },
    onSuccess: () => {
      useSession.getState().signOut();
      navigate(`/signin?next=${encodeURIComponent(`/consent?request=${id}`)}`, { replace: true });
      queryClient.clear();
    },
  });

  if (id === "") return <ConsentError code="invalid_request" />;
  if (request.isPending) return null;
  if (request.isError) {
    return (
      <AuthScreen title="Connection request">
        <FormError>{errorText(request.error)}</FormError>
      </AuthScreen>
    );
  }

  const view: View = request.data;
  const host = view.redirectHost;

  const back = (label: string) => (
    <button type="button" disabled={busy} onClick={() => decide.mutate(false)} className={bigSecondary}>
      {label}
    </button>
  );
  const failure = decide.isError && <FormError>{errorText(decide.error)}</FormError>;

  if (view.problem !== null || view.connection === null) {
    const problem = view.problem ?? "";
    return (
      <AuthScreen title="Can't connect">
        <p className="text-text-body">{problems[problem] ?? "This request can't be completed."}</p>
        {signOut.isError && <FormError>Signing out didn't work. Try again.</FormError>}
        {failure}
        <div className="flex flex-col gap-3">
          {problem === "other_user" && (
            <button type="button" disabled={signOut.isPending} onClick={() => signOut.mutate()} className={primaryButtonClass}>
              Sign out
            </button>
          )}
          {back(problem === "revoked" || problem === "expired" ? "Back" : `Back to ${host}`)}
        </div>
      </AuthScreen>
    );
  }

  const { client, connection } = view;
  return (
    <AuthScreen title={view.renewal ? `Renew ${connection.name} for ${client.name}?` : `Connect ${client.name}?`}>
      <p className="text-lg leading-snug break-words text-text-body">
        <strong className="text-text">{client.name}</strong> ({client.host}) wants to use your connection <strong className="text-text">{connection.name}</strong>:
      </p>
      <ul className="flex flex-col divide-y divide-line rounded-[12px] border border-line bg-surface">
        {Object.entries(connection.vaults).map(([vault, right]) => (
          <li key={vault} className="px-4 py-3 break-words text-text">
            {vault}: {rightText(right)}
          </li>
        ))}
      </ul>
      {view.renewal && <p className="text-text-body">This renews the connection with the same vaults and rights.</p>}
      {connection.expiresAt && <p className="text-text-body">Expires {day.format(new Date(connection.expiresAt))}</p>}
      <p className="text-text-body">It returns to {host} afterwards.</p>
      {!client.verified && (
        <p role="note" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-text">
          This app registered itself; Kumbu² can't confirm who made it.
        </p>
      )}
      {failure}
      <div className="flex flex-col gap-3">
        <button type="button" disabled={busy} onClick={() => decide.mutate(true)} className={primaryButtonClass}>
          Allow
        </button>
        <button type="button" disabled={busy} onClick={() => decide.mutate(false)} className={bigSecondary}>
          Deny
        </button>
      </div>
    </AuthScreen>
  );
}
