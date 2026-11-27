// SPDX-License-Identifier: AGPL-3.0-or-later
// The mirror slot (offline mode design §4.6) as the Eidolon core's port: the one Kumbu²-only call the sealed store
// makes. Errors as the core's REST client throws them: AuthError for a 401, OfflineError when the server can't be
// reached or fails; the 404 with `mirror-wipe` is the answer "gone".
import { AuthError, OfflineError, RejectedError } from "eidolon-core/engine/ports";
import type { MirrorSlotsPort, SlotAnswer } from "eidolon-core/store/ports";
import { useSession } from "../state/session";

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

/** A device with a copy, as Settings, Offline lists them (§4.6). `current`: this browser's. `waiting` null: not known yet. */
export interface DeviceCopy { id: string; label: string; createdAt: string; contactAt: string | null; waiting: number | null; current: boolean; }

async function send(method: "GET" | "PUT" | "DELETE", path: string, body?: unknown): Promise<Response> {
  const json = body === undefined ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
  try {
    return await fetch(new Request(new URL(`${BASE}${path}`, window.location.origin), { method, credentials: "same-origin", ...json }));
  } catch {
    throw new OfflineError("Kumbu² can't be reached.");
  }
}

/** The devices with a copy of the user's notes. */
export async function listDeviceCopies(): Promise<DeviceCopy[]> {
  return answer<DeviceCopy[]>(await send("GET", ""));
}

/**
 * Whether removing a device's copy needs the password: every copy but this browser's own, whatever its last contact
 * said, as what it wrote since is the one thing in Kumbu² without a trash or a history (design §4.4, ADR 0042).
 */
export const needsPassword = (d: DeviceCopy) => !d.current;

/** Gives a device's copy another name in the list; the copy itself stays as it is. */
export async function renameDeviceCopy(id: string, label: string): Promise<void> {
  const response = await send("PUT", `/${encodeURIComponent(id)}/label`, { label });
  if (!response.ok) await answer(response);
}

/** Removes a device's copy: it goes when that device next comes online, with what it hadn't uploaded. */
export async function removeDeviceCopy(id: string, password?: string): Promise<void> {
  const path = `/${encodeURIComponent(id)}`;
  const response = password === undefined ? await send("DELETE", path) : await post(`${path}/remove`, { password });
  if (response.status === 404 || response.ok) return;
  await answer(response);
}

export const mirrorSlots: MirrorSlotsPort = {
  async create(label) {
    return answer<SlotAnswer & { token: string }>(await post("", { label }));
  },
  async contact(token, waiting) {
    const response = await post("/contact", waiting === null ? { token } : { token, waiting });
    if (response.status === 404) {
      const error = (await response.clone().json().catch(() => null)) as { code?: string } | null;
      if (error?.code === "mirror-wipe") return "gone";
    }
    return answer<SlotAnswer>(response);
  },
};

/**
 * The slot as the copies reach it: every contact passes here, whichever path started it (sign-in, page load, socket,
 * `mirror` frame), and goes out only with the token of the signed-in user's copy (§4.11: another user's token is never
 * handed in). Looked at last, just before the request: the user may have changed while the contact waited.
 */
export function signedInSlots(slots: MirrorSlotsPort, tokenOf: (userId: string) => Promise<string | null>): MirrorSlotsPort {
  return {
    create: (label) => slots.create(label),
    async contact(token, waiting) {
      const userId = useSession.getState().user?.id;
      const own = userId ? await tokenOf(userId) : null;
      if (own !== token || useSession.getState().user?.id !== userId) throw new AuthError("This offline copy isn't the signed-in user's.");
      return slots.contact(token, waiting);
    },
  };
}
