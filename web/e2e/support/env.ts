// SPDX-License-Identifier: AGPL-3.0-or-later
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const baseURL = "http://localhost:8080";
export const e2eDir = join(dirname(fileURLToPath(import.meta.url)), "..");
export const authDir = join(e2eDir, ".auth");
export const fixturesDir = join(e2eDir, "fixtures");
export const stackDir = join(e2eDir, "stack");

export type User = "alice" | "bob";
export const usernames: Record<User, string> = { alice: "alice", bob: "bob" };
export const storageState = (user: User) => join(authDir, `${user}.json`);

/** Random per run, made by the setup, kept in .auth/ (ignored by git and Docker), never logged. */
export type Secrets = { passwords: Record<User, string>; pin: string };

const secretsFile = join(authDir, "secrets.json");

export function writeSecrets(secrets: Secrets) {
  mkdirSync(authDir, { recursive: true });
  writeFileSync(secretsFile, JSON.stringify(secrets), { mode: 0o600 });
}

let cached: Secrets | undefined;
export function secrets(): Secrets {
  if (!cached) {
    if (!existsSync(secretsFile)) throw new Error("No secrets: the setup project hasn't run.");
    cached = JSON.parse(readFileSync(secretsFile, "utf8")) as Secrets;
  }
  return cached;
}
