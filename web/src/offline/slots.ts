// SPDX-License-Identifier: AGPL-3.0-or-later
// The mirror slot (offline mode design §4.6) as the Eidolon core's port: the one Kumbu²-only call the sealed store
// makes. Errors as the core's REST client throws them: AuthError for a 401, OfflineError when the server can't be
// reached or fails; the 404 with `mirror-wipe` is the answer "gone".
import { AuthError, OfflineError, RejectedError } from "eidolon-core/engine/ports";
import type { MirrorSlotsPort, SlotAnswer } from "eidolon-core/store/ports";

const BASE = "/api/account/mirror-slots";

async function post(path: string, body: unknown): Promise<Response> {
  try {
    return await fetch(`${BASE}${path}`, {
      method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
    });
  } catch {
    throw new OfflineError("Kumbu² can't be reached.");
  }
}

async function answer<T>(response: Response): Promise<T> {
  if (response.ok) return (await response.json()) as T;
  const error = (await response.json().catch(() => null)) as { error?: string; code?: string } | null;
  if (response.status === 401) throw new AuthError(error?.error ?? "Signed out.");
  if (response.status >= 500 || response.status === 429) throw new OfflineError(error?.error ?? `Kumbu² answered ${response.status}.`);
  throw new RejectedError(error?.error ?? `Kumbu² answered ${response.status}.`, response.status);
}

export const mirrorSlots: MirrorSlotsPort = {
  async create(label) {
    return answer<SlotAnswer & { token: string }>(await post("", { label }));
  },
  async contact(token, waiting) {
    const response = await post("/contact", { token, waiting });
    if (response.status === 404) {
      const error = (await response.clone().json().catch(() => null)) as { code?: string } | null;
      if (error?.code === "mirror-wipe") return "gone";
    }
    return answer<SlotAnswer>(response);
  },
};
