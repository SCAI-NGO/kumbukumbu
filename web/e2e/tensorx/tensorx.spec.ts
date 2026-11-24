// SPDX-License-Identifier: AGPL-3.0-or-later
// People's own TensorX keys (BYOK) in the browser: docs/superpowers/specs/2026-11-18-byok-embeddings-design.md, README
// "People's own TensorX keys (BYOK)", plan of the 0.7.0 tests, Task 9. The stack offers BYOK (compose.search.yaml:
// `Kumbu__Embeddings__Byok`, the default `internal`). The key typed in is a placeholder, `e2e-placeholder-byok-key`: the
// server checks it with one embeddings request to TensorX, which passes only because the VM's egress proxy replaces the
// Authorization header with the real key; the test never sees that key. A user made for the test (an admin invitation,
// support/users.ts), never Alice or Bob, whose key would change their vaults for every other test; deleted afterwards
// through the admin API with everything of theirs, the key removed first. Every request here goes to a paid service: one
// short note.
//
// Promises in scope, and the test that covers each (all in "BYOK: a saved key …"):
// - Settings, External, "Embeddings": the key typed in, checked first, a malformed one refused without being sent,
//   kept and never shown again ("TensorX key: saved, ends in …", never in an API answer), `GET /api/account/embeddings-key`
//   and the `tensorx-byok` provider's `needsKey` following it.
// - The New vault dialog with a key saved: "Search by meaning with TensorX (your own key)", ticked, its one line; the vault
//   starts on `tensorx-byok` and is embedded with the key; unticked, the vault starts on this server (`internal`).
// - "An imported vault starts on the server": the import makes its vaults with `POST /api/vaults` without a provider
//   (README "Export and import"); that call, with the key saved, gives `internal`. The import's own screens are left out:
//   they add nothing to the provider's choice.
// - Remove: the vaults on the key go back to this server (`internal`) and are embedded there again; the form is back.
// Left out:
// - The password window (`PUT /api/account/embeddings-key` needs it): it can't be closed within a run. A normal
//   session's window is always open (Sessions.cs:136), a remembered one's closes only after 30 minutes idle, and the
//   session-only check runs before the window's (Security.cs:183, 199), so a bearer gets 403, never the window's 401.
//   xUnit covers it: ByokApiTests.cs:247, PasswordWindowTests.cs:241-261.
// - "Move these N vaults to TensorX too?" beyond Not now (asserted to move nothing): Move would send the new account's
//   Welcome vault, eight notes, to the paid service.
// - A refused key (401/403 from TensorX) and "TensorX refused the key": the proxy replaces every key, so none is refused.
// - Replace, discreet vaults, shared vaults on the owner's key, BYOK switched off: xUnit (the BYOK spec's tests) and
//   other stacks.
import { request, type APIRequestContext } from "@playwright/test";
import { Api, encodePath } from "../support/api";
import { baseURL, type User } from "../support/env";
import { nextIndex, waitIndexed } from "../support/search";
import { expect, shot, shotsDir070, test } from "../support/test";
import { invitedUser, removeUser, type TestUser } from "../support/users";

test.use({ shotsDir: shotsDir070 });

// A placeholder, not a key (see above): 8 to 512 printable characters without spaces, as the server checks.
const placeholder = "e2e-placeholder-byok-key";

type Byok = { offered: boolean; keySaved: boolean; last4: string | null; refusedAt: string | null };
type Provider = { name: string; label: string; external: boolean; needsKey: boolean };
type Vault = { name: string; embeddings: string; level: string };

test("BYOK: a saved key is checked and kept, a new vault starts on it, removing it brings the vault back to the server @tensorx @search", async ({
  browser,
  projectContextOptions,
}, testInfo) => {
  // The join, a few page loads and two waits for the index (nextIndex each), and Alice's password check at the end.
  test.setTimeout(300_000);
  const user = await invitedUser(browser, projectContextOptions, "byok");
  let context: APIRequestContext | undefined;
  try {
    // The user's API, as the web app calls it: the session's cookie and the Origin every state-changing request needs.
    // The cast: `User` names Alice and Bob, and is used only for their password checks, which this API never makes.
    context = await request.newContext({ baseURL, storageState: await user.context.storageState(), extraHTTPHeaders: { Origin: baseURL } });
    const api = await Api.from(context, user.username as User);
    const page = user.page;
    const before = await api.json<Byok>("GET", "/api/account/embeddings-key");
    expect(before).toEqual({ offered: true, keySaved: false, last4: null, refusedAt: null });
    const byokBefore = (await api.json<Provider[]>("GET", "/api/embeddings")).find((p) => p.name === "tensorx-byok");
    expect(byokBefore).toMatchObject({ label: "TensorX (your own key)", external: true, needsKey: true });
    const movable = (await api.json<Vault[]>("GET", "/api/vaults")).filter((v) => v.level === "owner" && v.embeddings !== "tensorx-byok");

    await page.goto("/settings/external");
    const section = page.getByRole("region", { name: "Embeddings" });
    await expect(section.getByText(/With a TensorX key of your own, the vaults you choose are found with TensorX's model/)).toBeVisible();
    const field = section.getByLabel("TensorX key", { exact: true });
    await expect(field).toHaveAttribute("type", "password");
    // Malformed: refused at the boundary, before any request to TensorX.
    await field.fill("short");
    await section.getByRole("button", { name: "Save key" }).click();
    await expect(section.getByText("A TensorX key is 8 to 512 characters, letters, digits and punctuation, without spaces.")).toBeVisible();
    expect((await api.json<Byok>("GET", "/api/account/embeddings-key")).keySaved, "a malformed key isn't saved").toBe(false);

    await field.fill(placeholder);
    await section.getByRole("button", { name: "Save key" }).click();
    await expect(section.getByText("TensorX key: saved, ends in …-key", { exact: true })).toBeVisible();
    await expect(section.getByLabel("TensorX key", { exact: true }), "the field goes once the key is saved").toHaveCount(0);
    await expect(section.getByRole("button", { name: "Replace key" })).toBeVisible();
    // The first save asks once which of her vaults go along (a new account owns Welcome), all ticked; Not now moves none.
    expect(movable.map((v) => v.name)).toContain("Welcome");
    const offer = page.getByRole("dialog", { name: /^Move (this vault|these \d+ vaults) to TensorX too\?$/ });
    await expect(offer.getByRole("checkbox", { name: "Welcome" })).toBeChecked();
    await offer.getByRole("button", { name: "Not now" }).click();
    await expect(offer).toHaveCount(0);
    // Read back: saved, its last four characters only, and the key itself in no answer.
    const saved = await api.send("GET", "/api/account/embeddings-key");
    expect(await saved.json()).toEqual({ offered: true, keySaved: true, last4: "-key", refusedAt: null });
    const providers = await api.send("GET", "/api/embeddings");
    const vaultsAnswer = await api.send("GET", "/api/vaults");
    for (const answer of [saved, providers, vaultsAnswer]) expect((await answer.text()).includes(placeholder), `${answer.url()} holds the key`).toBe(false);
    expect(((await providers.json()) as Provider[]).find((p) => p.name === "tensorx-byok")?.needsKey).toBe(false);
    expect(((await vaultsAnswer.json()) as Vault[]).filter((v) => v.embeddings === "tensorx-byok"), "Not now moved nothing").toEqual([]);

    // The New vault dialog: TensorX offered, ticked, with its one line.
    const vault = `Byok-${user.username.slice(-6)}`;
    await page.goto("/");
    await page.getByRole("button", { name: /^(New vault|Create your first vault)$/ }).click();
    const dialog = page.getByRole("dialog", { name: "New vault" });
    await dialog.getByLabel("Name").fill(vault);
    const box = dialog.getByRole("checkbox", { name: "Search by meaning with TensorX (your own key)" });
    await expect(box).toBeChecked();
    await expect(dialog.getByText("Its text and the searches that include it go to TensorX. Unticked, it stays on this server.", { exact: true })).toBeVisible();
    // The sheet fades in: the screenshot once it has.
    await expect.poll(() => page.evaluate(() => document.getAnimations().filter((a) => a.playState === "running").length)).toBe(0);
    await shot(page, testInfo, "tensorx");
    await dialog.getByRole("button", { name: "Create vault" }).click();
    await expect(dialog).toHaveCount(0);
    const created = (await api.json<Vault[]>("GET", "/api/vaults")).find((v) => v.name === vault);
    expect(created?.embeddings, "the new vault starts on the key").toBe("tensorx-byok");

    // Unticked, the vault stays on this server.
    const unticked = `Local-${user.username.slice(-6)}`;
    await page.goto("/");
    await page.getByRole("button", { name: /^(New vault|Create your first vault)$/ }).click();
    await dialog.getByLabel("Name").fill(unticked);
    await box.uncheck();
    await dialog.getByRole("button", { name: "Create vault" }).click();
    await expect(dialog).toHaveCount(0);
    // As the import makes its vaults: no provider named.
    const imported = `Imported-${user.username.slice(-6)}`;
    await api.json("POST", "/api/vaults", { name: imported });
    const both = await api.json<Vault[]>("GET", "/api/vaults");
    expect(both.find((v) => v.name === unticked)?.embeddings, "an unticked vault starts on the server").toBe("internal");
    expect(both.find((v) => v.name === imported)?.embeddings, "a vault made without a provider, as the import does, starts on the server").toBe("internal");

    // Embedded with the key: `similar` answers for the note (one request to TensorX).
    const note = `/${vault}/Bees/Hive.md`;
    await api.json("PUT", `/api/notes/${encodePath(note)}`, {
      content: "# Hive\n\nThe beekeeper opens the hive and lifts out the frames to check the brood, the queen and the stores of honey.\n",
    });
    await waitIndexed(api, [note], nextIndex);

    // Remove: the vault goes back to this server.
    await page.goto("/settings/external");
    await section.getByRole("button", { name: "Remove key" }).click();
    const removing = section.getByRole("group", { name: "Remove the key" });
    await expect(removing.getByText("The vaults that use it go back to this server and are embedded here again; nothing more is sent to TensorX.")).toBeVisible();
    await removing.getByRole("button", { name: "Remove", exact: true }).click();
    await expect(section.getByLabel("TensorX key", { exact: true }), "the form is back").toBeVisible();
    expect(await api.json<Byok>("GET", "/api/account/embeddings-key")).toEqual({ offered: true, keySaved: false, last4: null, refusedAt: null });
    const after = (await api.json<Vault[]>("GET", "/api/vaults")).find((v) => v.name === vault);
    expect(after?.embeddings, "removing the key brings the vault back to the server").toBe("internal");
    // Embedded again by the server's model: `similar` compares within the vault's current model only.
    await waitIndexed(api, [note], nextIndex);
    expect((await api.json<Provider[]>("GET", "/api/embeddings")).find((p) => p.name === "tensorx-byok")?.needsKey).toBe(true);
  } finally {
    await cleanUp(context, user);
  }
});

/** The key removed, then the user deleted with all of theirs by Alice (Admin → Users), so the stack keeps no state of the test. */
async function cleanUp(context: APIRequestContext | undefined, user: TestUser) {
  try {
    try {
      if (context) {
        const api = await Api.from(context, user.username as User);
        if ((await api.json<Byok>("GET", "/api/account/embeddings-key")).keySaved) await api.json("DELETE", "/api/account/embeddings-key");
      }
    } finally {
      await context?.dispose();
    }
  } finally {
    await user.context.close();
    await removeUser(user.username);
  }
}
