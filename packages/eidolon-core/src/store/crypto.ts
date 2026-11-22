// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed mirror's cryptography (offline mode design §4.1, §4.2). The host's WebCrypto comes in as a port, in the
// shape of the few calls used here, so the core sees no DOM: a browser passes `crypto.subtle`, Node its webcrypto's.
//
// One mirror key (AES-256, made by the server, §4.6) per user and browser. Two keys are derived from it with HKDF and
// imported non-extractable, so script can use them but never read them: one seals records (AES-GCM, a fresh nonce
// per record, the record's storage key as additional data, so a record moved to another key no longer opens), one
// names them (HMAC-SHA-256 of the vault id and the path, so storage keys say nothing of either).

/** A CryptoKey: opaque here. */
export type Key = object;

type Bytes = Uint8Array<ArrayBuffer>;
type Usage = "encrypt" | "decrypt" | "sign" | "deriveKey";

export interface Subtle {
  importKey(format: "raw", keyData: Bytes, algorithm: "HKDF" | "PBKDF2", extractable: false, usages: Usage[]): Promise<Key>;
  deriveKey(
    algorithm: { name: "HKDF"; hash: "SHA-256"; salt: Bytes; info: Bytes } | { name: "PBKDF2"; hash: "SHA-256"; salt: Bytes; iterations: number },
    baseKey: Key, derivedKeyType: { name: "AES-GCM"; length: 256 } | { name: "HMAC"; hash: "SHA-256"; length: 256 },
    extractable: false, usages: Usage[]): Promise<Key>;
  encrypt(algorithm: { name: "AES-GCM"; iv: Bytes; additionalData: Bytes }, key: Key, data: Bytes): Promise<ArrayBuffer>;
  decrypt(algorithm: { name: "AES-GCM"; iv: Bytes; additionalData: Bytes }, key: Key, data: Bytes): Promise<ArrayBuffer>;
  sign(algorithm: "HMAC", key: Key, data: Bytes): Promise<ArrayBuffer>;
}

export interface CryptoPort {
  readonly subtle: Subtle;
  /** Bytes from the host's secure random source (`crypto.getRandomValues`). */
  randomBytes(length: number): Bytes;
}

/** About a second on a mid-range phone (design §4.2); the header keeps the count, so it can be raised later. */
export const PBKDF2_ITERATIONS = 1_000_000;

const NONCE_BYTES = 12;
const encoder = new TextEncoder();

export interface MirrorKeys { readonly seal: Key; readonly name: Key; }

/** The record keys from the raw mirror key. The caller wipes `raw` afterwards; the keys can't be read back. */
export async function deriveKeys(c: CryptoPort, raw: Bytes): Promise<MirrorKeys> {
  const base = await c.subtle.importKey("raw", raw, "HKDF", false, ["deriveKey"]);
  const hkdf = (info: string) => ({ name: "HKDF" as const, hash: "SHA-256" as const, salt: new Uint8Array(0), info: encoder.encode(info) });
  const [seal, name] = await Promise.all([
    c.subtle.deriveKey(hkdf("kumbu-mirror records"), base, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]),
    c.subtle.deriveKey(hkdf("kumbu-mirror names"), base, { name: "HMAC", hash: "SHA-256", length: 256 }, false, ["sign"]),
  ]);
  return { seal, name };
}

/** nonce ‖ AES-GCM(plain), bound to `aad`. */
export async function seal(c: CryptoPort, key: Key, plain: Bytes, aad: string): Promise<Bytes> {
  const iv = c.randomBytes(NONCE_BYTES);
  const sealed = new Uint8Array(await c.subtle.encrypt({ name: "AES-GCM", iv, additionalData: encoder.encode(aad) }, key, plain));
  const out = new Uint8Array(NONCE_BYTES + sealed.byteLength);
  out.set(iv);
  out.set(sealed, NONCE_BYTES);
  return out;
}

/** The plaintext, or null when the record doesn't open: another key, another `aad`, or changed bytes. */
export async function open(c: CryptoPort, key: Key, sealed: Bytes, aad: string): Promise<Bytes | null> {
  if (sealed.byteLength < NONCE_BYTES) return null;
  try {
    const iv = sealed.slice(0, NONCE_BYTES);
    return new Uint8Array(await c.subtle.decrypt({ name: "AES-GCM", iv, additionalData: encoder.encode(aad) }, key, sealed.slice(NONCE_BYTES)));
  } catch {
    return null;
  }
}

/** A record's name: HMAC of `label`, Base64Url. */
export async function nameOf(c: CryptoPort, key: Key, label: string): Promise<string> {
  return toBase64Url(new Uint8Array(await c.subtle.sign("HMAC", key, encoder.encode(label))));
}

/** The mirror key sealed under a key from the password, for unlocking offline (design §4.2). Base64Url throughout. */
export interface PasswordWrap { salt: string; iterations: number; wrapped: string; }

async function passwordKey(c: CryptoPort, password: string, salt: Bytes, iterations: number): Promise<Key> {
  const base = await c.subtle.importKey("raw", encoder.encode(password.normalize("NFC")), "PBKDF2", false, ["deriveKey"]);
  return c.subtle.deriveKey({ name: "PBKDF2", hash: "SHA-256", salt, iterations }, base, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
}

/** `aad` binds the wrap to its copy, so a wrap copied into another copy's header doesn't open. A fresh salt each time. */
export async function wrapWithPassword(c: CryptoPort, raw: Bytes, password: string, aad: string, iterations = PBKDF2_ITERATIONS): Promise<PasswordWrap> {
  const salt = c.randomBytes(16);
  const key = await passwordKey(c, password, salt, iterations);
  return { salt: toBase64Url(salt), iterations, wrapped: toBase64Url(await seal(c, key, raw, aad)) };
}

/** The raw mirror key, or null for a wrong password. */
export async function unwrapWithPassword(c: CryptoPort, wrap: PasswordWrap, password: string, aad: string): Promise<Bytes | null> {
  const salt = fromBase64Url(wrap.salt);
  const wrapped = fromBase64Url(wrap.wrapped);
  if (salt === null || wrapped === null) return null;
  return open(c, await passwordKey(c, password, salt, wrap.iterations), wrapped, aad);
}

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

export function toBase64Url(bytes: Uint8Array): string {
  let out = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | ((bytes[i + 1] ?? 0) << 8) | (bytes[i + 2] ?? 0);
    out += ALPHABET[n >> 18]! + ALPHABET[(n >> 12) & 63]!;
    if (i + 1 < bytes.length) out += ALPHABET[(n >> 6) & 63]!;
    if (i + 2 < bytes.length) out += ALPHABET[n & 63]!;
  }
  return out;
}

/** Null for anything but unpadded Base64Url, so a bad header or server answer can't be half read. */
export function fromBase64Url(text: string): Bytes | null {
  if (text.length % 4 === 1) return null;
  const out = new Uint8Array(Math.floor((text.length * 3) / 4));
  let bits = 0;
  let value = 0;
  let at = 0;
  for (const ch of text) {
    const n = ALPHABET.indexOf(ch);
    if (n < 0) return null;
    value = (value << 6) | n;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[at++] = (value >> bits) & 255;
    }
  }
  return out;
}
