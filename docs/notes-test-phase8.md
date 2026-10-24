# Test notes: phase 8 (accounts backend)

Date: 2026-10-25. Built locally while Chris was away (branch `step-8`, from the plan `docs/superpowers/plans/2026-10-24-accounts-backend.md` and the spec `docs/superpowers/specs/2026-10-24-accounts-and-web-design.md`), 10 tasks, each implemented by a subagent and reviewed by another, then a final whole-branch review and one fix wave. `dotnet test`: 974 passed, ten full runs in a row without a failure.

The stack was rebuilt from the branch on the existing data (phase 7b's vaults, embeddings and the dev token), with `KUMBU_PUBLIC_URL=http://localhost:8083`.

## Results

### Upgrade from step 7b

| Check | Result |
|---|---|
| Start with existing data | The three dev-token vaults unlock; the log names them by id (`Unlocked vault 4e506a42…`), no longer by name |
| Seeding | `left 3 vaults alone`, as before |
| `kumbu bootstrap-admin` | Prints `http://localhost:8083/join#<code>`, valid 24 h; the code is in the fragment, so it never reaches the server's or a proxy's log |

### A user's way in

| Step | Result |
|---|---|
| Peek the bootstrap invitation | `role: primary_admin`, expiry, `valid: true` |
| Join with `password1234` | 400 `This password is a common one with digits or punctuation around it.` |
| Join with a good passphrase | 200, user `chris` (primary admin), the recovery key in this response only, signed in |
| The same code again | 400 `This invitation has been used already.` |
| Sign in: wrong password / unknown user | Both 401 `The username or password is wrong.` |
| A second sign-in | `previousSignInAt` is the first one (for the agent banner) |

### Vaults, upload and notes

| Step | Result |
|---|---|
| Create `Arbeit`, again | 201, then 409 `You already have a vault named 'Arbeit'.` |
| Upload two notes plus `.obsidian/app.json`, `Bilder/logo.png`, `../boese.md` (ask) | 2 written; the other three skipped, each with its reason |
| The same upload again (ask) | Nothing written, both listed as conflicts with their versions |
| Replace with identical content | Skipped: `the note has this content already` |
| Tree, read, links, backlinks | As the `sh` commands show them; `[[Anna]]` resolves within the vault |
| `PUT` with version, then with the stale one | 200; 409 with the current content and version (for the conflict toast) |
| `PUT` without `Origin` | 403 `This request didn't come from Kumbu's own pages.` |
| Search hybrid „wer ist die Architektin" | `Anna.md` first, `score 0.51`, reason `meaning`, snippet the matching line |
| Search exact „Envoy" | The architecture section, reason `exact` |

### API keys over MCP

| Step | Result |
|---|---|
| Create `claude-laptop` (write) and `reader` (read) | Shown once, with prefix; a key named `chris` refused: `its writes would look like your own` |
| `ls /` with a key | Only `Arbeit/`: the dev-token vaults (`/Demo`) are not reachable (`No such file or directory`) |
| `edit` with the write key | Works (with the version token); the history names the key as writer, in a changeset of its own |
| `rm` and `write` with the read key | `rm: this key can only read vault Arbeit`, `write: …` |
| `semsearch` with the read key | Works, with scores |
| Admin endpoint with an admin's key | 403 `This needs a signed-in session; API keys can't manage accounts, keys or users.` |
| Revoke, then use | 401 at once |
| `/api/changes` after a key's write | The note, with the key's name as writer (the agent banner's data) |

### A second user

| Step | Result |
|---|---|
| Invitation for `lea` | Link with the code in the fragment, note „für Lea" |
| Lea joins | Her own account, her own recovery key |
| Lea's vaults; Chris's note by path | `[]`; 404, the same answer as for a note that doesn't exist |
| Lea creates `Arbeit` | 201: vault names are per user |
| Lea on `/api/admin/users` | 403 `Only an admin can do this.` |
| Chris's admin list | Name, role, active, created, last sign-in, number of vaults, bytes — no vault names |

### Secrets

| Check | Result |
|---|---|
| Recovery key regeneration without / with a wrong password | 400; 400 `The current password is wrong.` |
| The server log after all of this | No vault name, note name, password, API key or invitation code |

## Decisions taken on Chris's behalf

Recorded as rulings in the plan workspace during the build; listed so they can be undone:

1. The common-password check also refuses a common word padded with digits or punctuation (the 10k list alone let `password1234` through at 12 characters).
2. Passwords NFC-normalised before Argon2; usernames ASCII only (no look-alikes such as Cyrillic `а`); one password and one recovery slot per user enforced by an index.
3. Sessions refused if their credential was read before a password change (`<=`, as MongoDB keeps milliseconds), and the change stamps before and after writing the new password slot.
4. Session cookies accepted under `/api` only, not on `/mcp` (CSRF from a same-site subdomain); under `/api` and `/mcp` only explicitly mapped endpoints can be anonymous, never a fallback.
5. The dev token compared through hashes; it counts only its own vaults as locked.
6. A rate-limit permit reserved before the asynchronous key lookup, so a burst of wrong keys can't slip past the limit.
7. API keys may not be named like their owner, `dev`, `seed`, or contain ` (`, control or format characters.
8. Deactivating a user revokes their keys and sessions; reactivating does so again (closing a race), and reactivating an active user changes nothing.
9. A redeemed invitation is given back only for expected refusals (a taken username), never after an unknown error, so one code can't make two accounts; creating the primary admin revokes other open bootstrap links.
10. The plaintext `owner` field on user slots (to show vaults and bytes per user to admins): a database reader learns which vaults belong to whom, not their names or content. To state in the CTO document.
11. Invitation codes moved into the link fragment and the peek to a POST body; ASP.NET's request logging at Warning.
12. `Cache-Control: no-store` on every `/api` response; the server refuses to start with `KUMBU_TRUSTED_PROXY` but without `KUMBU_PUBLIC_URL`.
13. Regenerating the recovery key needs the current password (a stolen session could otherwise take the account over).
14. Vault ids instead of names in logs; a global Argon2 concurrency limit (one run per core).
15. Search reasons and snippets as in the mobile editor spec (`title`, `exact`, `all words`, `meaning`; the first line with a query word).
16. `DELETE` of a note returns 204; the web app warns before deleting, from the backlinks.

Parked for later: re-authentication for admin invitations (a stolen admin session can create a lasting admin account, though it can't open anyone's vaults); a password change doesn't revoke API keys (documented: check your keys after a suspected compromise); a reverse proxy's access log contains note paths and search queries (documented: filter it).

## Suggested next step

1. The web app (plan `docs/superpowers/plans/2026-10-24-web-app.md`), on top of this API.
2. Then the public deployment and the CTO document (plan `docs/superpowers/plans/2026-10-24-deployment-and-cto-doc.md`); Chris's Traefik network and resolver names are still needed.
