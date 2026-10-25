// SPDX-License-Identifier: AGPL-3.0-or-later
import createClient, { type Middleware } from "openapi-fetch";
import { useSession } from "../state/session";
import type { paths } from "./schema";

/** A refusal or fault from the API; `message` is the server's sentence for the user, if it sent one. */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

// These answer 401 for wrong credentials, not for a missing session; the screens show that themselves.
const anonymous = new Set(["POST /api/session", "POST /api/join", "POST /api/recover", "POST /api/invitations/peek"]);

const sessionEnds: Middleware = {
  onResponse({ request, response, schemaPath }) {
    if (response.status === 401 && !anonymous.has(`${request.method} ${schemaPath}`)) useSession.getState().end();
  },
};

export const api = createClient<paths>({
  // Absolute, as Request wants outside the browser too; same origin either way.
  baseUrl: window.location.origin,
  credentials: "include",
  // Looked up per call, so tests can stub it.
  fetch: (request) => globalThis.fetch(request),
});
api.use(sessionEnds);

/** The data of an openapi-fetch call, or an HttpError with the server's `{ error }` sentence. */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok) {
    const message = error && typeof error === "object" && "error" in error && typeof error.error === "string" ? error.error : "";
    throw new HttpError(response.status, message);
  }
  return data as T;
}
