// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * The build this bundle came from, `0.2.0+a6362ec`, the same string the server sends as `X-Kumbu-Version`
 * (both are built with it, Dockerfile). `0.0.0-dev` in a local build: updates are then not looked for.
 */
export const appVersion: string = __KUMBU_VERSION__;

export type Version = { semver: string; sha: string };

/** A release or pre-release with its commit; null for anything else, which switches update detection off. */
export function parseVersion(text: string | null | undefined): Version | null {
  const plus = text?.indexOf("+") ?? -1;
  if (!text || plus <= 0 || plus === text.length - 1) return null;
  const semver = text.slice(0, plus);
  if (semver.startsWith("0.0.0-dev")) return null;
  return { semver, sha: text.slice(plus + 1) };
}

/**
 * The server's version when it is a different build from this app's, else null. Different, not greater: a
 * rollback is an update too, and the server is what the app must match.
 */
export function newerOnServer(app: string, server: string | null): Version | null {
  const ours = parseVersion(app);
  const theirs = parseVersion(server);
  return ours && theirs && app !== server ? theirs : null;
}
