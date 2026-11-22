// SPDX-License-Identifier: AGPL-3.0-or-later
// Turning offline on and off on this device (design §3a, "Turning it on", "Settings, Offline").
import { IDLE_MS, MirrorCopies, REMEMBERED_IDLE_MS, type CopyHeader } from "eidolon-core/store/mirror";
import type { StorePorts } from "eidolon-core/store/ports";
import { api, call } from "../api/client";
import { useOfflineCopy } from "./copy";
import { deviceLabel } from "./deviceLabel";
import { browserPorts, keepThroughCleanUps } from "./ports";
import { removeDeviceCopy } from "./slots";

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
  await call(api.POST("/api/session/confirm", { body: { password } }));
  const mirror = await new MirrorCopies(await storePorts()).create({
    userId: user.id, username: user.username, password, label: deviceLabel(), idleMs: remembered() ? REMEMBERED_IDLE_MS : IDLE_MS,
  });
  useOfflineCopy.getState().set(mirror.header, mirror);
  await keepThroughCleanUps().catch(() => false);
}

/** The copy of this user on this device, opened with the slot's key while signed in; null without one. */
export async function openCopy(userId: string): Promise<void> {
  const copies = new MirrorCopies(await storePorts());
  const header = await copies.forUser(userId);
  const state = useOfflineCopy.getState();
  if (state.mirror !== null && !state.mirror.locked && header !== null && state.header?.copy === header.copy) return;
  // Another user's copy held by this tab (someone else signed in here): it closes before anything of this one opens.
  if (state.header !== null && state.header.copy !== header?.copy) {
    state.mirror?.lock();
    useOfflineCopy.getState().clear();
  }
  if (header === null) return;
  const opened = await copies.unlockOnline(header);
  if (opened === "wiped") useOfflineCopy.getState().clear();
  else useOfflineCopy.getState().set(opened.header, opened);
}

/**
 * Removes the copy from this device, with any changes it hadn't uploaded, and its place in the device list. Offline,
 * the list keeps it until it is removed there; the copy goes either way.
 */
export async function turnOff(header: CopyHeader): Promise<void> {
  const mirror = useOfflineCopy.getState().mirror;
  if (mirror !== null && !mirror.locked) await mirror.remove();
  else await new MirrorCopies(await storePorts()).wipe(header.copy);
  useOfflineCopy.getState().clear();
  await removeDeviceCopy(header.slot.id).catch(() => {});
}
