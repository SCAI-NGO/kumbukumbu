# Architecture decision records

Each file here records one decision that shapes Kumbu²'s architecture, security model, data format or a lasting behaviour: its context, the decision, why, and what follows. [0001](0001-record-architecture-decisions.md) says why we keep them.

## How to add one

1. Copy [`template.md`](template.md) to `NNNN-short-title.md`, with the next free number.
2. Fill it in while the decision is fresh, the reasons and the options that lost included. One decision per record; link the spec for the detail.
3. Add it to the list below, in the same pull request as the change it decides.
4. A decision that changes an earlier one is a new record: mark the earlier one `Superseded by [NNNN](…)`, and leave its text as it was.

Records are public. A security decision says what we do and why, never which confidential review finding led to it.

## Records

| # | Decision | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-eidolon-core-as-a-host-agnostic-package.md) | Build the client's data layer as the Eidolon core, a host-agnostic package | Accepted |
| [0003](0003-the-browser-syncs-as-an-obsidian-device.md) | Treat the browser's copy exactly as an Obsidian device | Accepted |
| [0004](0004-offline-is-opt-in-per-device.md) | Make offline opt-in per device | Accepted |
| [0005](0005-offline-unlock-by-password.md) | Unlock the offline copy with the password, never a PIN | Accepted |
| [0006](0006-argon2id-through-a-kdf-port.md) | Derive the offline wrap with Argon2id, through a KDF port | Accepted |
| [0007](0007-a-version-counter-for-sealed-records.md) | Protect the offline copy's records against rollback with a version counter | Accepted |
| [0008](0008-password-change-keeps-other-devices-copies.md) | Keep other devices' offline copies on a password change, and show them | Accepted |
| [0009](0009-password-to-remove-a-copy-with-pending-changes.md) | Ask for the password before removing another device's copy with changes waiting | Accepted |
| [0010](0010-embeddings-stay-on-the-server.md) | Keep embeddings on the server; offline searches by words | Accepted |
| [0011](0011-large-attachments-on-demand.md) | Fetch large attachments on demand | Accepted |
| [0012](0012-no-move-or-rename-offline.md) | No moving or renaming while offline in 0.7.0 | Accepted |
| [0013](0013-discreet-vaults-are-never-mirrored.md) | Never keep a discreet vault offline | Accepted |
| [0014](0014-not-related-as-a-note-property.md) | Store "Not related" as a note property | Accepted |
| [0015](0015-unconfirmed-members-see-similarity.md) | Show similarity to vault members who haven't confirmed the embedding provider | Accepted |
| [0016](0016-synthesize-knowledge-as-a-system-skill.md) | Ship synthesize-knowledge as a system skill | Accepted |
| [0017](0017-published-is-a-window-of-its-own.md) | Published is a window of its own, with the bar's actions | Accepted |
| [0018](0018-compare-a-copy-by-its-note-version.md) | Compare a published copy by the note version it was made from | Proposed |
| [0019](0019-deactivation-removes-offline-copies.md) | Deactivating a user removes their offline copies | Proposed |
| [0020](0020-another-users-token-is-refused-not-wiped.md) | Answer a contact with another user's token with a refusal, not a wipe | Proposed |
| [0021](0021-the-service-worker-checks-what-it-serves.md) | Let the service worker serve a kept file only while its bytes are the build's | Proposed |
| [0022](0022-one-module-wires-the-offline-mode.md) | One module wires the offline mode together | Proposed |
| [0023](0023-queries-run-whatever-the-browser-says.md) | Queries and mutations run whatever the browser says about the network | Proposed |
| [0024](0024-the-browsers-offline-closes-the-socket.md) | The browser's "offline" closes the sync socket | Proposed |
| [0028](0028-hubs-without-tag-count-only-kept-links.md) | `graph --hubs --without-tag` counts only the links of the notes kept | Accepted |

0002 to 0016 were recorded after the fact, on 2026-11-23, from the decisions of the 0.7.0 weekend; their dates are the days they were taken.
