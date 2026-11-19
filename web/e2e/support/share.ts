// SPDX-License-Identifier: AGPL-3.0-or-later
// A vault of a spec's own that Alice shares with Bob, for tests where Bob must write (spec §3). Through the calls the
// web app makes, with API contexts only, so the `mcp` project stays without a browser. In beforeAll/afterAll only.
import { randomBytes } from "node:crypto";
import { Api } from "./api";
import { secrets } from "./env";
import { reservePassword } from "./session";

export type Share = { vault: string; membershipId: string };

/** Alice's new vault `<name>-<random>`, shared with Bob at `role`; Bob has accepted it under its own name. */
export async function shareWithBob(name: string, role: "read" | "write" | "maintain"): Promise<Share> {
  const vault = `${name}-${randomBytes(3).toString("hex")}`;
  const alice = await Api.as("alice");
  const bob = await Api.as("bob");
  let membershipId: string | undefined;
  try {
    await alice.json("POST", "/api/vaults", { name: vault });
    try {
      // The invitation checks the password window and the password in its body (SharingApi.cs:82,98-101); confirming
      // counts against the same limit, so only once the window has closed.
      const invite = async () => {
        await reservePassword("alice");
        return alice.send("POST", `/api/vaults/${encodeURIComponent(vault)}/invitations`, {
          data: { password: secrets().passwords.alice, label: "Bob", role, expiresInDays: 1, confirm: false },
        });
      };
      let response = await invite();
      if (response.status() === 401) {
        await alice.confirmPassword();
        response = await invite();
      }
      // Never the body: the link holds the invitation's secret.
      if (!response.ok()) throw new Error(`POST /api/vaults/${vault}/invitations: ${response.status()}`);
      const { link } = (await response.json()) as { id: string; link: string };
      // What follows `#` in the link, as the share page sends it (web/src/routes/Share.tsx).
      const secret = new URL(link).hash.slice(1);
      const accepted = await bob.send("POST", "/api/vault-invitations/accept", { data: { secret, alias: vault } });
      if (!accepted.ok()) throw new Error(`POST /api/vault-invitations/accept: ${accepted.status()} ${await accepted.text()}`);
      const { id, status } = (await accepted.json()) as { id: string; status: string };
      membershipId = id;
      if (status !== "active") throw new Error(`Bob's membership of ${vault} is ${status}, not active`);
      return { vault, membershipId: id };
    } catch (error) {
      // A failed share leaves nothing behind that a later test's vault list would show.
      if (membershipId) await bob.send("POST", `/api/memberships/${encodeURIComponent(membershipId)}/leave`);
      await reservePassword("alice");
      await alice.send("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { data: { password: secrets().passwords.alice, confirm: vault } });
      throw error;
    }
  } finally {
    await alice.dispose();
    await bob.dispose();
  }
}

/**
 * Bob leaves first, so no "The owner has deleted …" notice (MemberNotices.cs:111) reaches his later pages; then Alice
 * deletes the vault: smoke #1 and #8 assert the exact vault lists after the `mcp` project ran.
 */
export async function unshare({ vault, membershipId }: Share) {
  const alice = await Api.as("alice");
  const bob = await Api.as("bob");
  try {
    try {
      await bob.json("POST", `/api/memberships/${encodeURIComponent(membershipId)}/leave`);
    } finally {
      await reservePassword("alice");
      await alice.json("POST", `/api/vaults/${encodeURIComponent(vault)}/delete`, { password: secrets().passwords.alice, confirm: vault });
    }
  } finally {
    await alice.dispose();
    await bob.dispose();
  }
}
