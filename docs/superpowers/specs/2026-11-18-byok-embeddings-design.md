# BYOK embeddings — design

Approved by Chris on 2026-11-18. Size: small (server and one settings section).

## Why

On a server that embeds with its own model, people who want better search by meaning can use their own TensorX key. TensorX's embeddings (Qwen3-Embedding 8B) rank better than arctic-m (`docs/notes-search-external.md`). The operator doesn't pay for it and doesn't have to configure anything. Decided by Chris on 2026-11-18. His reason: the people on his reference instance should get the better embeddings without the instance paying for them.

## The three deployments

Decided by Chris on 2026-11-18. His reason: together these cover every sensible use.

| Deployment | `KUMBU_EMBEDDINGS_DEFAULT` | `KUMBU_EMBEDDINGS_BYOK` | Result |
|---|---|---|---|
| a) Self-hosted embeddings only | `internal` | `false` (the default) | As today. |
| b) The operator pays | an external provider | ignored | Everyone uses the operator's provider. BYOK is never offered. |
| c) Self-hosted by default, own key allowed | `internal` | `true` | People may enter their own TensorX key. |

`Kumbu:Embeddings:Byok` (Compose: `KUMBU_EMBEDDINGS_BYOK`) is the admin's switch. It takes effect only while the default is `internal`. When the default is external, the server logs once at startup that the setting is ignored, and starts normally.

## Decisions

- **TensorX is built in as the BYOK provider** (approach A). The base URL `https://api.tensorx.ai/v1`, the model `qwen/qwen3-embedding-8b`, the 1024 dimensions and the query prompt are taken from the README's example and fixed in the code. People enter only their key.
  - Decided by Chris. His reason: every new upstream needs curation anyway, which is the experience from Chatsune and Chatsundere, so a configurable template buys nothing.
  - It is also a security decision: people never name a URL, so the server can't be pointed at an internal address (no SSRF).
- **The key belongs to the account.** People enter it once, in the settings.
  - Decided by Chris. His reason: one key, one bill.
- **The choice is made per vault,** with the existing "Search by meaning…" dialog. It lists "TensorX (your own key)" next to the internal model and any providers the admin configured.
- **New vaults use the better provider when chosen.** When the account has a key, the New vault dialog offers TensorX, ticked, and says in one line that the vault's text goes there; the vault starts on TensorX only with that choice (`POST /api/vaults` with `provider`). A vault created without one, as an import does, starts on this server.
  - Decided by Chris. His reason: TensorX's embeddings are better.
  - Changed by Chris on 2026-11-19: only a named provider puts a vault on TensorX. His reason: honest by default, a vault's text leaves the server only on a choice that was shown.
- **Existing vaults are not moved silently.** After someone saves a key, the app asks once: "Move these N vaults to TensorX too?". It lists the vaults with checkboxes, all ticked, and offers "Not now".
  - My call: from then on the vaults' text leaves the server, and people have to choose that consciously (the honesty principle in the design principles).
- **Discreet vaults never use BYOK.** A discreet vault's text stays on the server. The option doesn't appear for a discreet vault, and the API refuses it.
  - Proposed on 2026-11-13 and kept.
- **Shared vaults use the owner's key.** Members already see where the vault's text goes, from the existing notice. Nothing new is needed.

## How the key is kept

- **On the account:** the key is sealed with the account's own key, the same way the account's other secrets are wrapped. It is never shown again. The settings show "TensorX key: saved, ends in …abcd" with Replace and Remove.
- **In each vault that uses it:** a copy is sealed into the vault's meta, next to `Embeddings`. Why: the server has to embed when the owner isn't signed in, for example when a member of a shared vault writes. The vault's own key is available exactly when the vault is unlocked, so that is the right lifetime for the copy.
- **Replace:** the new key is written to the account and to every vault that holds a copy.
- **Remove:** the key is deleted from the account and from every vault's meta. Those vaults switch to `internal` and are embedded again in the background. Until then they are found by words.
- **Never logged,** never returned by any API, and never in history or a support dump.

## How it runs

- A BYOK vault's embeddings carry their own model key, `tensorx-byok:qwen/qwen3-embedding-8b@1024` (or something similar). The search index never compares vectors across models; this keeps that rule as it already is.
- The existing `OpenAiCompatibleDriver` does the requests. It takes the key per request, from the vault, instead of once at construction. The retries, backoff, pause and fallback to words already exist and stay as they are.
- **A rejected key** (401 or 403) does not pause the provider for everyone; that pause stays for 429 and 5xx. Only this account's vaults go to words search. `semsearch` says so in its footer, as it does for a paused provider. The settings show "TensorX rejected the key", with the time.
- **The admin turns BYOK off later:** BYOK vaults fall back to `internal` the next time they are unlocked, and are embedded again in the background. The keys stay sealed in place, so turning BYOK back on restores everything.
  - My call: a vault should never be left without search by meaning because of a switch its owner didn't touch.

## API

- `PUT /api/account/embeddings-key` with the body `{ "key": "…" }`: save or replace the key. The key is checked first with one small embeddings request; a rejected key is not saved. Sessions only; agents can't set it.
- `DELETE /api/account/embeddings-key`: remove the key.
- `GET /api/embeddings` adds `byok: { offered, keySaved, last4, rejectedAt }` to its answer.
- `PUT /api/vaults/{vault}/embeddings` accepts `{ "provider": "tensorx-byok" }`. The server refuses it when BYOK isn't offered, when the account has no key, when the vault is discreet, or when the caller isn't the vault's owner.

Every endpoint validates its input at the boundary. The key is printable ASCII, 8 to 512 characters.

## Not in this version

- Other BYOK providers: each one needs curation, and each one is new code.
- Agents setting the key over MCP. Why: the key is a credential and belongs in the settings, where a person types it.
- A usage or cost display: TensorX's own dashboard has that.

## Tests (written first)

xUnit:
- the validator for the three deployments (b ignores the switch);
- the vault meta round trip with the key copy;
- Replace and Remove reaching every vault;
- discreet vaults refused;
- non-owners refused;
- a rejected key isolates that account only;
- BYOK switched off falls back to `internal`;
- new vaults start on BYOK when the dialog's box is ticked;
- the key never appears in logs, API answers or history.

Vitest:
- the settings section;
- the "move these vaults" prompt;
- the new-vault line;
- the provider dialog.

## README and docs

- README: the new setting, the three deployments as a short table, and the privacy line (the text goes to TensorX under the person's own contract).
- `docs/security-architecture.md` §5.5: where the key lives and who can reach it.

## Left to the builder

The builder's calls (2026-11-18):

- **The model key** is `tensorx-byok:qwen/qwen3-embedding-8b@1024`. Every person's key shares it, so a replaced key re-embeds nothing.
- **A driver per key** (`ByokDrivers`), cached by a 16-hex SHA-256 prefix of the key, its *route*. The index has a queue per route and the search one query per route (`EmbeddingProviders.Resolve` and `RouteOf`), so one key's pause holds up only its own vaults.
- **Where the account's key lives:** the user document's `embeddingsKey`, sealed under a key derived from the user key, as the discreet list is (`EmbeddingsKeys`). It is resealed on every rotation of the user key, as that list is.
- **The status is a separate endpoint** rather than a field of `GET /api/embeddings`, because that endpoint answers with an array: `GET /api/account/embeddings-key` answers `{ offered, keySaved, last4, refusedAt }`. The list gains a `tensorx-byok` entry with `needsKey` while no key is saved.
- **"Refused at"** is kept in memory and resets at a restart. It is a hint for the settings, not a record.
- **BYOK off** falls back in memory at unlock (`VaultCatalog.Provider`) and leaves the meta as it is, so switching BYOK on again restores the vaults without a write.
- **Marking an owned BYOK vault discreet** switches it to `internal` first. Its members get the usual "provider changed" event, as for any switch.
- **The settings section** sits under Settings, Vaults, as "Search by meaning", and is shown only where BYOK is offered. The "move these vaults" prompt lists the person's own vaults that are not discreet, not being re-encrypted and not already on TensorX.
- **A key id decides whether a vault's copy counts.** Each saved key gets a random id, kept in the clear on the user document (`embeddingsKeyId`); each vault's copy carries the id it was made with, and runs on TensorX only while that is the owner's id now, else on `internal`, the dead copy cleared at its next unlock. Replace writes a new id first, Remove clears it first, then both update the copies they can reach; a vault they can't write (being re-encrypted, busy) is skipped, not waited for. Why: Remove and Replace can't reach every copy (a vault being re-encrypted, one open nowhere), and must never fail half-done; the id kills every unreachable copy at once, without the user key, and random rather than derived from the key, it tells nothing about the key.
- **A vault known to be discreet** (its owner's session found it on her list, or she just marked it) never resolves to BYOK in the index or the search, whatever its meta says. Marking a vault discreet clears the choice and the copy from the stored meta, also while BYOK is switched off.
- **The New vault dialog** shows a ticked "Search by meaning with TensorX (your own key)" box. Its choice goes with the create (`POST /api/vaults` takes `provider`, checked as `PUT …/embeddings` checks it), so an unticked vault is never embedded with the key.
- **`web/src/api/schema.d.ts`** gets only the new paths and schemas, from a generated copy. A full regeneration would also bring in drift from earlier pull requests, and that drift breaks the build.
