// SPDX-License-Identifier: AGPL-3.0-or-later
// Turning offline on and off on this device (design §3a, "Turning it on", "Settings, Offline").
import { AuthError } from "eidolon-core/engine/ports";
import { IDLE_MS, MirrorCopies, REMEMBERED_IDLE_MS, type CopyHeader } from "eidolon-core/store/mirror";
import type { StorePorts } from "eidolon-core/store/ports";
import { api, call } from "../api/client";
import { forgetLastNoteOf } from "../shell/lastNote";
import { useSession } from "../state/session";
import { useOfflineCopy } from "./copy";
import { deviceLabel } from "./deviceLabel";
import { browserPorts, keepThroughCleanUps } from "./ports";
import { removeDeviceCopy } from "./slots";
import { keep, switchBegun, switchEnded } from "./mode";

let ports: Promise<StorePorts> | null = null;
/** The browser's ports, the database opened once per tab. */
export function storePorts(): Promise<StorePorts> {
  ports ??= browserPorts();
  return ports;
}

const rememberedKey = "kumbu.remembered";

/** Whether "Keep me signed in" was ticked at this device's last sign-in: the copy's idle limit follows it (§3a). */
export function remembered(): boolean {
  try {
    return localStorage.getItem(rememberedKey) === "1";
  } catch {
    return false;
  }
}

/** At every sign-in: no secret, only which idle limit applies here. */
export function rememberSignIn(remember: boolean): void {
  try {
    if (remember) localStorage.setItem(rememberedKey, "1");
    else localStorage.removeItem(rememberedKey);
  } catch {
    // Not kept: the shorter limit applies.
  }
}

/**
 * Turns offline on for the signed-in user. The password is checked first: the copy's offline wrap is made from it,
 * and a mistyped one would leave a copy that never opens offline. Then the browser is asked to keep it (§5).
 */
export async function turnOn(user: { id: string; username: string }, password: string): Promise<void> {
  // Ended here whether it is made or fails in the dialog; only a reload leaves it begun.
  switchBegun(user.id);
  try {
    await made(user, password);
  } finally {
    switchEnded();
  }
}

async function made(user: { id: string; username: string }, password: string): Promise<void> {
  await call(api.POST("/api/session/confirm", { body: { password } }));
  const ports = await storePorts();
  const signedIn = () => useSession.getState().user?.id === user.id;
  // The slot is made under the session that goes with the request: only while this user is the one signed in, looked
  // at just before it (ADR 0029), so their copy never gets a slot of someone else's.
  const slots = { ...ports.slots, create: (label: string) => (signedIn() ? ports.slots.create(label) : Promise.reject(new AuthError("Signed out."))) };
  const mirror = await new MirrorCopies({ ...ports, slots }).create({
    userId: user.id, username: user.username, password, label: deviceLabel(), idleMs: remembered() ? REMEMBERED_IDLE_MS : IDLE_MS,
  });
  // Someone else signed in while it was made: the copy stays, sealed, for its user.
  if (!signedIn()) return mirror.lock();
  keep(mirror);
  await keepThroughCleanUps().catch(() => false);
}

/**
 * Removes the copy from this device, with any changes it hadn't uploaded, and its place in the device list. Offline,
 * the list keeps it until it is removed there; the copy goes either way.
 */
export async function turnOff(header: CopyHeader): Promise<void> {
  // Read at once: while the contact waits, someone else may sign in and open their own copy in this tab.
  const mirror = useOfflineCopy.getState().mirror;
  const ports = await storePorts();
  // The slot is this browser's to remove without the password only once this session has handed in its token (a new
  // sign-in, a copy still locked): a contact first makes it so.
  await ports.slots.contact(header.slot.token, null).catch(() => {});
  forgetLastNoteOf(header.user);
  if (mirror !== null && !mirror.locked && mirror.header.copy === header.copy) await mirror.remove();
  else await new MirrorCopies(ports).wipe(header.copy);
  if (useOfflineCopy.getState().header?.copy === header.copy) useOfflineCopy.getState().clear();
  await removeDeviceCopy(header.slot.id).catch(() => {});
}
