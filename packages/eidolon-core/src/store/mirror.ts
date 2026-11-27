// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline copies in a browser and their unlocking (offline mode design §4.2, §4.4, §3a "Unlocking").
//
// A copy is one user's mirror in this browser. Its header (`h/<copy>`) is the only record in the clear: the schema,
// hashes of the user's id and of the username (two people may share a browser; offline only the username is known),
// the mirror slot's id and token (the token is kept in the clear by design, §4.6: the server's key needs the user key
// too), the mirror key wrapped under the password, and the idle limit of the last sign-in. Everything else is sealed
// (vault.ts). The key is only ever in the tab's memory.
import { DEFAULT_CHOICE, heldOnly, without, type OfflineChoice } from "./choice";
import { fromBase64Url, toBase64Url, unwrapWithPassword, wrapWithPassword, type PasswordWrap } from "./crypto";
import type { SlotAnswer, SlotVault, StorePorts, TabMessage } from "./ports";
import { MirrorLockedError, Records, VaultMirror, type VaultMeta } from "./vault";

export interface CopyHeader {
  schema: 2;
  copy: string;
  /** SHA-256 of the user id, Base64Url: finds the copy when signed in. */
  user: string;
  /** SHA-256 of the username, lower case: finds the copy at an offline sign-in. */
  login: string;
  slot: { id: string; token: string };
  password: PasswordWrap;
  /** How long an unlocked copy stays open without use: the session's idle limit (§3a). */
  idleMs: number;
}

/** What a contact did: vaults purged and the changes lost with them (the notice names neither vault, §3a). */
export interface Purged { vaults: number; lost: number; }

/** The session's idle limits (`Accounts/Sessions.cs`): 30 minutes, 7 days with "Keep me signed in". */
export const IDLE_MS = 30 * 60_000;
export const REMEMBERED_IDLE_MS = 7 * 24 * 3600_000;

const HEADERS = "h/";
const encoder = new TextEncoder();

async function hashOf(label: string): Promise<string> {
  return toBase64Url(new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(label))));
}

const userHash = (userId: string) => hashOf(`kumbu-mirror user\0${userId}`);
const loginHash = (username: string) => hashOf(`kumbu-mirror login\0${username.normalize("NFC").toLowerCase()}`);
const wrapAad = (copy: string) => `kumbu-mirror wrap\0${copy}`;
/** The copy's lock across tabs, as its records take it (Records.lockName). */
const lockName = (copy: string) => `kumbu-mirror:${copy}`;

/** Whether the copy is this user's: by the hash of the user id in its header, as `forUser` finds it. */
export async function ownedBy(header: CopyHeader, userId: string): Promise<boolean> {
  return header.user === (await userHash(userId));
}

/** The server's ids and tokens are Base64Url or hex: nothing else goes into a request header. */
const TOKEN = /^[A-Za-z0-9_-]+$/;
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null;
const isString = (v: unknown): v is string => typeof v === "string";

/**
 * A header with every field of its type, or null. The idle limit is held to the longest a session has: a shorter one
 * only locks sooner.
 */
function inShape(h: unknown): CopyHeader | null {
  if (!isObject(h) || h.schema !== 2 || !isString(h.copy) || !TOKEN.test(h.copy) || !isString(h.user) || !isString(h.login)) return null;
  const { slot, password, idleMs } = h;
  if (!isObject(slot) || !isString(slot.id) || !TOKEN.test(slot.id) || !isString(slot.token) || !TOKEN.test(slot.token)) return null;
  if (!isObject(password) || !isObject(password.kdf) || !isString(password.salt) || !isString(password.wrapped)) return null;
  if (typeof idleMs !== "number" || !Number.isFinite(idleMs)) return null;
  return { ...(h as unknown as CopyHeader), idleMs: Math.min(idleMs, REMEMBERED_IDLE_MS) };
}

function mirrorKey(answer: SlotAnswer): Uint8Array<ArrayBuffer> {
  const raw = fromBase64Url(answer.key);
  if (raw === null || raw.byteLength !== 32) throw new Error("The server's mirror key is not an AES-256 key.");
  return raw;
}

export class MirrorCopies {
  constructor(private readonly p: StorePorts) {}

  async headers(): Promise<CopyHeader[]> {
    const found: CopyHeader[] = [];
    for (const [, value] of await this.p.storage.scan(HEADERS)) {
      // The one record in the clear, so anyone with the disk can change it: one that doesn't parse, or isn't in
      // shape, is passed over.
      let parsed: unknown;
      try {
        parsed = JSON.parse(new TextDecoder().decode(value));
      } catch {
        continue;
      }
      const header = inShape(parsed);
      if (header !== null) found.push(header);
    }
    return found;
  }

  async forUser(userId: string): Promise<CopyHeader | null> {
    const hash = await userHash(userId);
    return (await this.headers()).find((h) => h.user === hash) ?? null;
  }

  async forUsername(username: string): Promise<CopyHeader | null> {
    const hash = await loginHash(username);
    return (await this.headers()).find((h) => h.login === hash) ?? null;
  }

  /**
   * Turns offline on for a signed-in user: a new mirror slot, its key wrapped under the password. A copy of this user
   * already here is replaced, its unsynced changes with it: the caller looks for one first and unlocks it instead.
   */
  async create(o: { userId: string; username: string; password: string; label: string; idleMs: number }): Promise<SealedMirror> {
    // Before the slot: a failure here leaves none behind on the server.
    const old = await this.forUser(o.userId);
    const answer = await this.p.slots.create(o.label);
    const raw = mirrorKey(answer);
    if (old !== null) await this.wipe(old.copy);
    const copy = toBase64Url(this.p.crypto.randomBytes(16));
    const header: CopyHeader = {
      schema: 2, copy, user: await userHash(o.userId), login: await loginHash(o.username),
      slot: { id: answer.id, token: answer.token },
      password: await wrapWithPassword(this.p.crypto, raw, o.password, wrapAad(copy)), idleMs: o.idleMs,
    };
    await this.writeHeader(header);
    return SealedMirror.unlocked(this.p, header, raw, answer.vaults);
  }

  /**
   * Unlocks with the slot's key, for a signed-in tab; nothing to ask. At a sign-in the password and the idle limit
   * come too: the key is wrapped again under the password (a changed password needs nothing else, §4.2). "wiped":
   * the slot was removed, and the copy with it.
   */
  async unlockOnline(header: CopyHeader, signIn?: { username: string; password: string; idleMs: number }): Promise<SealedMirror | "wiped"> {
    const answer = await this.p.slots.contact(header.slot.token, null);
    if (answer === "gone") {
      await this.wipe(header.copy);
      return "wiped";
    }
    const raw = mirrorKey(answer);
    if (signIn !== undefined) {
      header = {
        ...header, login: await loginHash(signIn.username), idleMs: signIn.idleMs,
        password: await wrapWithPassword(this.p.crypto, raw, signIn.password, wrapAad(header.copy)),
      };
      if (!(await this.rewriteHeader(header))) {
        raw.fill(0);
        return "wiped";
      }
    }
    const mirror = await SealedMirror.unlocked(this.p, header, raw, answer.vaults);
    // The first contact couldn't count them, before the key opened the copy: the device list hears of them now, none
    // included. If that fails, the copy stays open; the device list says "not known yet" until the next contact.
    try {
      if ((await mirror.contact()) === "wiped") return "wiped";
    } catch {
      // Offline or signed out between the two: the next contact (socket, sign-in) counts them.
    }
    return mirror;
  }

  /** Unlocks with the password, offline (§3a: the sign-in form opens the copy). Null for a wrong password. */
  async unlockOffline(header: CopyHeader, password: string): Promise<SealedMirror | null> {
    const raw = await unwrapWithPassword(this.p.crypto, header.password, password, wrapAad(header.copy));
    return raw === null ? null : SealedMirror.unlocked(this.p, header, raw, null);
  }

  /** Removes a copy with everything in it: a wipe signal, the slot removed, or the person's own choice. */
  async wipe(copy: string): Promise<void> {
    await this.p.locks.run(lockName(copy), () => this.p.storage.write({ remove: [`${HEADERS}${copy}`], removePrefix: [`c/${copy}/`] }));
    this.p.channel(copy).post({ type: "purged", vault: null });
  }

  private async writeHeader(header: CopyHeader): Promise<void> {
    await this.p.storage.write({ put: [[`${HEADERS}${header.copy}`, encoder.encode(JSON.stringify(header))]] });
  }

  /** An existing copy's header, written again only while the copy is still here: another tab may have removed it. */
  private rewriteHeader(header: CopyHeader): Promise<boolean> {
    return this.p.locks.run(lockName(header.copy), async () => {
      if ((await this.p.storage.get(`${HEADERS}${header.copy}`)) === null) return false;
      await this.writeHeader(header);
      return true;
    });
  }
}

/**
 * An unlocked copy: its vaults' mirrors, the contact that keeps them in line with the server, purging. Locking drops
 * the key and everything read; a locked copy is opened again from its header.
 */
export class SealedMirror {
  private readonly vaults = new Map<string, VaultMirror>();
  /** The vaults the last contact listed, null before one (offline). */
  private listed: Map<string, SlotVault> | null = null;
  private stopListening: (() => void) | null = null;
  private engines: { release(): void } | null = null;
  private chosen: OfflineChoice = DEFAULT_CHOICE;
  /** Another tab changed paths of a vault, or purged one (vault null: the whole copy). */
  onChanged: ((vault: string, paths: string[]) => void) | null = null;
  onPurged: ((vault: string | null) => void) | null = null;
  /**
   * A vault's mirror is going (purged here or in another tab): the host stops what runs on it. Awaited before the
   * records go when the purge is this tab's.
   */
  onDrop: ((vault: string) => Promise<void>) | null = null;
  /** Another tab changed what this device keeps offline. */
  onChoice: (() => void) | null = null;
  private removedListener: ((removed: Purged) => void) | null = null;
  /** Removed before the host listened: at the unlocking's contacts. */
  private untold: Purged = { vaults: 0, lost: 0 };
  /**
   * Another tab signed out: the host writes what its editors hold into the copy and locks. Without a host's answer
   * the copy locks at once.
   */
  onLockAsked: (() => void) | null = null;

  private constructor(private readonly p: StorePorts, private h: CopyHeader, private readonly r: Records) {}

  /** The copy's header as this tab last wrote or read it. */
  get header(): CopyHeader { return this.h; }

  static async unlocked(p: StorePorts, header: CopyHeader, raw: Uint8Array<ArrayBuffer>, vaults: SlotVault[] | null): Promise<SealedMirror> {
    const r = await Records.unlock(p, header.copy, raw);
    raw.fill(0);
    const mirror = new SealedMirror(p, header, r);
    for (const [key, sealed] of await p.storage.scan(`${r.prefix}m/`)) {
      const meta = await r.openJson<VaultMeta>(key, sealed);
      if (meta !== null) mirror.vaults.set(meta.id, await VaultMirror.open(r, meta));
    }
    mirror.chosen = (await r.openJson<OfflineChoice>(await mirror.choiceKey(), await p.storage.get(await mirror.choiceKey()))) ?? DEFAULT_CHOICE;
    if (vaults !== null) await mirror.align(vaults);
    // A vault chosen and not held may be re-keyed (purged by align, adopted again by the engines): its entries stay.
    // With All my vaults that is any vault listed, and any at all offline, where nothing says which. Online, a chosen
    // vault the listing no longer has is gone for good: its entries go.
    if (!mirror.chosen.all || vaults !== null) {
      const listed = vaults === null ? null : new Set(vaults.map((v) => v.id));
      const chosen = mirror.chosen.all ? [...listed!] : mirror.chosen.vaults.filter((id) => listed === null || listed.has(id));
      const keep = new Set([...mirror.vaults.keys(), ...chosen]);
      if (heldOnly(mirror.chosen, keep) !== mirror.chosen) await mirror.choose((c) => heldOnly(c, keep));
    }
    // A lock while a message is handled ends that work; nothing is left to report.
    mirror.stopListening = r.channel.listen((m) => void mirror.heard(m).catch((e: unknown) => {
      if (!mirror.locked) throw e;
    }));
    return mirror;
  }

  get locked(): boolean { return this.r.locked; }

  /** A contact went through since this copy was unlocked: what it may keep is the slot's word, not only the vault list's. */
  get contacted(): boolean { return this.listed !== null; }

  /** The vaults this device holds a mirror of. */
  held(): VaultMirror[] {
    this.assertOpen();
    return [...this.vaults.values()];
  }

  vault(id: string): VaultMirror | undefined {
    this.assertOpen();
    return this.vaults.get(id);
  }

  /**
   * Starts mirroring a vault the last contact listed. A vault it didn't list is never mirrored, a discreet one above
   * all (§4.3): the server leaves those out.
   */
  async adopt(id: string): Promise<VaultMirror> {
    this.assertOpen();
    const held = this.vaults.get(id);
    if (held !== undefined) return held;
    const listed = this.listed?.get(id);
    if (listed === undefined) throw new Error("Only a vault the server lists for this copy is mirrored.");
    const meta = await VaultMirror.writeMeta(this.r, { id, tag: listed.tag, canWrite: listed.canWrite, notesDone: false });
    const vault = await VaultMirror.open(this.r, meta);
    this.vaults.set(id, vault);
    return vault;
  }

  /** What this device keeps offline (Settings, Offline). */
  get choice(): OfflineChoice {
    this.assertOpen();
    return this.chosen;
  }

  /** Changes the choice, on the copy's latest across tabs, and tells the other tabs. */
  async choose(change: (choice: OfflineChoice) => OfflineChoice): Promise<OfflineChoice> {
    this.assertOpen();
    const key = await this.choiceKey();
    return this.p.locks.run(this.r.lockName, async () => {
      const now = (await this.r.openJson<OfflineChoice>(key, await this.p.storage.get(key))) ?? DEFAULT_CHOICE;
      const next = change(now);
      await this.p.storage.write({ put: [await this.r.sealJson(key, next)] });
      this.chosen = next;
      this.r.channel.post({ type: "choice" });
      return next;
    });
  }

  /** The changes waiting to upload, across the copy's vaults. */
  waiting(): number {
    return [...this.vaults.values()].reduce((n, v) => n + v.waiting(), 0);
  }

  /**
   * Makes contact (on sign-in, every socket (re)connect and each `mirror` frame, §4.6): reports the changes waiting,
   * learns which vaults may stay, and purges the rest. "wiped": the slot is gone, so is the copy, and this mirror
   * is locked. Throws AuthError and OfflineError: nothing changes then.
   */
  async contact(): Promise<Purged | "wiped"> {
    this.assertOpen();
    const answer = await this.p.slots.contact(this.header.slot.token, this.waiting());
    if (answer === "gone") {
      await this.remove();
      return "wiped";
    }
    return this.align(answer.vaults);
  }

  /**
   * A sign-in made while the copy stays open (the host's Sign in again, §4.2): the key, from the slot as at an online
   * unlock, is wrapped again under the password just given, so the copy opens offline with that one; the idle limit
   * stays the copy's. It is a contact too, with its answers.
   */
  async rewrap(username: string, password: string): Promise<Purged | "wiped"> {
    this.assertOpen();
    const answer = await this.p.slots.contact(this.h.slot.token, this.waiting());
    if (answer === "gone") {
      await this.remove();
      return "wiped";
    }
    const raw = mirrorKey(answer);
    const header: CopyHeader = {
      ...this.h, login: await loginHash(username), password: await wrapWithPassword(this.p.crypto, raw, password, wrapAad(this.h.copy)),
    };
    raw.fill(0);
    // Another tab may have removed the copy while the server and the password wrap were asked.
    const written = await this.p.locks.run(this.r.lockName, async () => {
      if ((await this.p.storage.get(`${HEADERS}${header.copy}`)) === null) return false;
      await this.p.storage.write({ put: [[`${HEADERS}${header.copy}`, encoder.encode(JSON.stringify(header))]] });
      return true;
    });
    if (!written) {
      this.lock();
      return "wiped";
    }
    this.h = header;
    return this.align(answer.vaults);
  }

  /**
   * Told of every vault's mirror that went because the server no longer gives it to this device (not listed by a
   * contact or the vault list, re-keyed), in this tab or another, whichever path found it first: the host's notice
   * follows the removal (§3a). Not of one the device no longer chooses to keep. Removals made before it listened, at
   * the unlocking, are told at once.
   */
  listenRemoved(fn: (removed: Purged) => void): void {
    this.removedListener = fn;
    if (this.untold.vaults > 0) this.removed(this.untold);
    this.untold = { vaults: 0, lost: 0 };
  }

  private removed(removed: Purged): void {
    if (this.removedListener !== null) this.removedListener(removed);
    else this.untold = { vaults: this.untold.vaults + removed.vaults, lost: this.untold.lost + removed.lost };
  }

  /**
   * Drops one vault's mirror, its unsynced changes too. Returns how many changes went with it. `left`: for the
   * server's reasons (told, listenRemoved); false for the device's own choice. `forGood`: its part of the device's
   * choice goes in the same write; false for a vault re-keyed, which comes again under its new tag.
   */
  async purge(id: string, left = true, forGood = true): Promise<number> {
    this.assertOpen();
    const vault = this.vaults.get(id);
    if (vault === undefined) return 0;
    const lost = vault.waiting();
    vault.drop();
    this.vaults.delete(id);
    await this.onDrop?.(id);
    const key = await this.choiceKey();
    let readopted = false;
    const chosen = await this.p.locks.run(this.r.lockName, async () => {
      const now = (await this.r.openJson<OfflineChoice>(key, await this.p.storage.get(key))) ?? DEFAULT_CHOICE;
      const next = forGood ? without(now, id) : now;
      const put = next === now ? [] : [await this.r.sealJson(key, next)];
      // Another tab may have purged and adopted it again already: a re-key leaves that new life alone.
      const stored = await this.r.openJson<{ epoch?: string }>(vault.metaKey, await this.p.storage.get(vault.metaKey));
      readopted = !forGood && stored !== null && stored.epoch !== vault.epoch;
      await this.p.storage.write(readopted ? { put } : { put, remove: [vault.metaKey], removePrefix: [vault.dataPrefix] });
      return next === now ? null : next;
    });
    if (readopted) await this.reopen(id);
    if (chosen !== null) {
      this.chosen = chosen;
      this.r.channel.post({ type: "choice" });
    }
    this.r.channel.post(left ? { type: "purged", vault: id, left: { lost } } : { type: "purged", vault: id });
    if (left) this.removed({ vaults: 1, lost });
    return lost;
  }

  /** Removes the whole copy from this device (Settings, Offline; a wipe), and locks. */
  async remove(): Promise<void> {
    this.lock();
    await new MirrorCopies(this.p).wipe(this.header.copy);
  }

  /**
   * Waits to be the tab that runs the copy's sync engines: one engine per vault across the browser's tabs (§3a). The
   * other unlocked tabs read the same records and hear of every change; their own writes reach the engines' tab as
   * "changed" (onChanged), which tells its engines.
   */
  lead(): Promise<void> {
    this.assertOpen();
    this.engines?.release();
    const held = this.p.locks.hold(`kumbu-mirror-engines:${this.header.copy}`);
    this.engines = held;
    return held.acquired;
  }

  /**
   * Drops the key and what was read: idle, signed out, or wiped. Unsaved editor text is the host's to write first;
   * the engines' tab stops its engines first and hands the engines over to another unlocked tab.
   */
  lock(): void {
    this.stopListening?.();
    this.stopListening = null;
    this.engines?.release();
    this.engines = null;
    for (const vault of this.vaults.values()) vault.drop();
    this.vaults.clear();
    this.r.lock();
  }

  private async align(vaults: SlotVault[]): Promise<Purged> {
    this.listed = new Map(vaults.map((v) => [v.id, v]));
    const purged: Purged = { vaults: 0, lost: 0 };
    for (const vault of [...this.vaults.values()]) {
      const now = this.listed.get(vault.id);
      // Not listed: access lost, deleted or marked discreet, alike by design. Another tag: re-keyed, synced afresh.
      if (now === undefined || now.tag !== vault.tag) {
        purged.lost += await this.purge(vault.id, true, now === undefined);
        purged.vaults++;
      } else if (now.canWrite !== vault.canWrite) {
        await vault.setMeta({ canWrite: now.canWrite });
      }
    }
    return purged;
  }

  /** Locks the copy in every tab that has it open: a sign-out, made in one tab, ends it in all. */
  lockEverywhere(): void {
    if (!this.locked) this.r.channel.post({ type: "locked" });
    this.lock();
  }

  private async heard(m: TabMessage): Promise<void> {
    if (this.locked) return;
    if (m.type === "choice") {
      const key = await this.choiceKey();
      const chosen = await this.r.openJson<OfflineChoice>(key, await this.p.storage.get(key));
      if (chosen !== null && !this.locked) this.chosen = chosen;
      this.onChoice?.();
      return;
    }
    if (m.type === "locked") {
      if (this.onLockAsked !== null) this.onLockAsked();
      else this.lock();
      return;
    }
    if (m.type === "purged") {
      if (m.vault === null) this.lock();
      else {
        const held = this.vaults.get(m.vault);
        held?.drop();
        this.vaults.delete(m.vault);
        await this.onDrop?.(m.vault);
        // Not again where this tab's own contact removed it first.
        if (held !== undefined && m.left !== undefined) this.removed({ vaults: 1, lost: m.left.lost });
        // Synced afresh at another tab's opening, its meta still stored: this tab takes up the new life. A purge
        // removed the meta, so nothing opens.
        await this.reopen(m.vault);
      }
      this.onPurged?.(m.vault);
      return;
    }
    const vault = this.vaults.get(m.vault);
    if (vault === undefined) {
      // Adopted in another tab.
      await this.reopen(m.vault);
    } else {
      try {
        vault.assertHeld();
        await vault.refresh(m.paths);
      } catch (e) {
        if (!(e instanceof MirrorLockedError) || this.locked) throw e;
        // Purged and adopted again in another tab, and this one missed the purge: it takes up the new life.
        if (this.vaults.get(m.vault) === vault) this.vaults.delete(m.vault);
        await this.onDrop?.(m.vault);
        await this.reopen(m.vault);
      }
    }
    this.onChanged?.(m.vault, m.paths);
  }

  private async choiceKey(): Promise<string> {
    return `${this.r.prefix}o/${await this.r.name("choice")}`;
  }

  private async reopen(id: string): Promise<void> {
    const keys = await this.r.vaultNames(id);
    const meta = await this.r.openJson<VaultMeta>(keys.meta, await this.p.storage.get(keys.meta));
    if (meta !== null && !this.locked) this.vaults.set(id, await VaultMirror.open(this.r, meta));
  }

  private assertOpen(): void {
    if (this.r.locked) throw new MirrorLockedError();
  }
}
