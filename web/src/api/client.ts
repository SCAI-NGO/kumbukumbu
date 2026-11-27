// SPDX-License-Identifier: AGPL-3.0-or-later
import { ownedBy, type CopyHeader } from "eidolon-core/store/mirror";
import createClient, { type Middleware } from "openapi-fetch";
import { useOfflineCopy } from "../offline/copy";
import { useOfflineMode, wipeCopy } from "../offline/mode";
import { endPendingSignOut, signOutPending } from "../offline/unlock";
import { usePasswordPrompt } from "../state/passwordPrompt";
import { useSession } from "../state/session";
import { useUpdate } from "../update/store";
import type { paths } from "./schema";

/**
 * A refusal or fault from the API; `message` is the server's sentence for the user, if it sent one, `type` its
 * machine-readable kind, and `code` a write refusal's reason (collab design §3.5): see `RefusalCode`.
 */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly type?: string,
    readonly code?: string,
  ) {
    super(message);
  }
}

/** Why the server refused a write: the level is below `write`, below `maintain` for the vault's structure, the vault is closing, or its owner is deactivated. */
export type RefusalCode = "read-only" | "structural" | "closing" | "owner-inactive";
const refusalCodes: readonly string[] = ["read-only", "structural", "closing", "owner-inactive"];

export const isRefusal = (error: unknown): error is HttpError & { code: RefusalCode } =>
  error instanceof HttpError && error.code !== undefined && refusalCodes.includes(error.code);

/**
 * Told of every refused write: the caller's level changed since the vault list was read (a role lowered, the owner
 * deactivated), so the shell reads it again and the write actions follow.
 */
const refusalListeners = new Set<(error: HttpError) => void>();
export function onRefusal(listener: (error: HttpError) => void) {
  refusalListeners.add(listener);
  return () => void refusalListeners.delete(listener);
}

// These answer 401 for wrong credentials, not for a missing session; the screens show that themselves.
const anonymous = new Set(["POST /api/session", "POST /api/join", "POST /api/recover", "POST /api/invitations/peek", "POST /api/pairing/redeem"]);

/** A remembered session whose password window has closed: a guarded action wants the password again, the session lives on. */
export async function isPasswordRequired(response: Response): Promise<boolean> {
  if (response.status !== 401) return false;
  const body: unknown = await response.clone().json().catch(() => null);
  return !!body && typeof body === "object" && "type" in body && body.type === "password-required";
}

/** The request handed in the offline copy's slot, which is gone: the copy goes too (offline mode design §4.6). */
export async function isMirrorWipe(response: Response): Promise<boolean> {
  if (response.status !== 401) return false;
  const body: unknown = await response.clone().json().catch(() => null);
  return !!body && typeof body === "object" && "code" in body && body.code === "mirror-wipe";
}

/** A 401 for a missing session ends it. `key`: "PUT /api/files/{vault}/{path}"; `token`: the copy's, if the request carried it. */
async function endOnLostSession(key: string, token: string | null, response: Response) {
  if (response.status !== 401 || anonymous.has(key) || (await isPasswordRequired(response))) return;
  if (await isMirrorWipe(response)) return void (await wipeCopy(token));
  // Back from offline with changes waiting: the bar asks to sign in again over the app, which keeps the place (§3a).
  const mode = useOfflineMode.getState();
  if (mode.offline && mode.waiting > 0) return mode.sessionLost();
  // After a sign-out, the page being left may still ask (or hear back) for a moment; those 401s are the sign-out
  // itself. Asking for the session is the way back in, and still leads to sign-in.
  if (useSession.getState().signedOut && key !== "GET /api/session") return;
  // The copy locks with it (the session store's rule): no way back past the sign-in page without the password, and
  // the next user never inherits it.
  useSession.getState().end();
}

const sessionEnds: Middleware = {
  onResponse: ({ request, response, schemaPath }) => endOnLostSession(`${request.method} ${schemaPath}`, request.headers.get("Kumbu-Mirror"), response),
};

/** Gateways answer these while Kumbu² behind them is down. Kumbu²'s own 503 (a vault being re-encrypted, the server busy
 * for a moment) carries its version, and reached it. */
const unreachable = (response: Response) => [502, 503, 504].includes(response.status) && !response.headers.has("X-Kumbu-Version");

/**
 * Whether a request may carry the copy's slot token (ADRs 0029, 0033): under the session of the copy's own user, or
 * with nobody signed in, but never with a request that signs in (`key` as above). After an offline sign-in the tab
 * knows the username only (id "", owning nothing) and the cookie may be someone else's: none goes until the session
 * is read.
 */
export async function carriesToken(header: CopyHeader, key: string): Promise<boolean> {
  const user = useSession.getState().user;
  if (user === null) return !anonymous.has(key);
  return ownedBy(header, user.id);
}

/**
 * What the offline mode needs to hear of every request (offline mode design §3a): whether it reached Kumbu²; and the
 * offline copy's slot token goes with it, so a 401 can say the copy was removed (§4.6).
 */
const reachWatch: Middleware = {
  async onRequest({ request, schemaPath }) {
    const header = useOfflineCopy.getState().header;
    if (header !== null && (await carriesToken(header, `${request.method} ${schemaPath}`))) request.headers.set("Kumbu-Mirror", header.slot.token);
    return request;
  },
  onResponse({ response }) {
    if (unreachable(response)) return void useOfflineMode.getState().failed();
    useOfflineMode.getState().reached();
    // Kumbu² answers again: a sign-out made while away reaches it now, not only at the next start.
    if (signOutPending()) void endPendingSignOut();
  },
  onError({ error }) {
    // fetch fails with a TypeError when it gets no answer; a cancelled request (an AbortError) says nothing.
    if (error instanceof TypeError) useOfflineMode.getState().failed();
  },
};

// Every API answer carries the server's build version: a different one means a release to reload into.
const versionWatch: Middleware = {
  onResponse({ response }) {
    useUpdate.getState().report(response.headers.get("X-Kumbu-Version"));
  },
};

/** What the middleware hears of every answer, for a request sent without openapi-fetch (an upload with progress). */
export async function heard(key: string, response: Response) {
  if (unreachable(response)) useOfflineMode.getState().failed();
  else useOfflineMode.getState().reached();
  useUpdate.getState().report(response.headers.get("X-Kumbu-Version"));
  // Sent without the copy's token.
  await endOnLostSession(key, null, response);
}

// What a guarded request sent, by openapi-fetch's request id, to send again once the password is confirmed. Reads are
// never guarded, so only the others are kept, and of those only the ones without a body or with a JSON one: every
// guarded action sends JSON, while an upload's bytes (a file, a form) would be copied for nothing, as no upload is guarded.
// Told apart by the Content-Type, not by `request.body`: Firefox has no `Request.body`, so a request without a body
// (Update copy of a single note) would never be kept there, and its refusal would never ask for the password.
const sent = new Map<string, Request>();

const repeatable = (request: Request) => {
  const type = request.headers.get("Content-Type");
  return request.method !== "GET" && (type === null || type.startsWith("application/json"));
};

/**
 * Asks for the password when a guarded action finds the window closed, and repeats the request once after it is
 * confirmed; the repeated answer goes back as it is. Cancelled, the action fails with the original answer.
 */
const passwordWindow: Middleware = {
  onRequest({ request, id }) {
    if (repeatable(request)) sent.set(id, request.clone());
  },
  async onResponse({ response, id }) {
    const clone = sent.get(id);
    sent.delete(id);
    if (!clone || !(await isPasswordRequired(response))) return;
    if (await usePasswordPrompt.getState().ask()) return globalThis.fetch(clone);
  },
  onError({ id }) {
    sent.delete(id);
  },
};

/** The `{**…}` parameters of the API: a path within a vault. */
const multiSegment = new Set(["path", "folder"]);

/**
 * `{path}` and `{folder}` (a path in its vault) keep their slashes, each segment encoded; any other value is one
 * segment. An empty `{folder}` (the vault's root) leaves its slash out too: `/api/upload/Work`.
 */
export function pathSerializer(pathname: string, params: Record<string, unknown>) {
  return pathname.replace(/\/?\{(\w+)\}/g, (match, name: string) => {
    const value = String(params[name]);
    const slash = match.startsWith("/") ? "/" : "";
    if (!multiSegment.has(name)) return slash + encodeURIComponent(value);
    return value === "" ? "" : slash + value.split("/").map(encodeURIComponent).join("/");
  });
}

export const api = createClient<paths>({
  // Absolute, as Request wants outside the browser too; same origin either way.
  baseUrl: window.location.origin,
  credentials: "include",
  pathSerializer,
  // Looked up per call, so tests can stub it.
  fetch: (request) => globalThis.fetch(request),
});
api.use(reachWatch);
api.use(sessionEnds);
api.use(versionWatch);
// After the two above: openapi-fetch hands answers to the last one first, so they see the repeated answer.
api.use(passwordWindow);

/** The data of an openapi-fetch call, or an HttpError with the server's `{ error }` sentence. */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok) {
    const message = error && typeof error === "object" && "error" in error && typeof error.error === "string" ? error.error : "";
    const type = error && typeof error === "object" && "type" in error && typeof error.type === "string" ? error.type : undefined;
    const code = error && typeof error === "object" && "code" in error && typeof error.code === "string" ? error.code : undefined;
    const refused = new HttpError(response.status, message, type, code);
    if (isRefusal(refused)) for (const listener of refusalListeners) listener(refused);
    throw refused;
  }
  return data as T;
}
