# Security and privacy

## Encrypted at rest

Every note, its history and the trash are encrypted before they are stored, and so are the notes' paths and the vaults' names. Each vault has a key of its own. A copy of the database or of a backup shows nothing readable.

## Your password and your recovery key

Your password unlocks your vaults, and so does your recovery key, shown once when you joined. Keep the recovery key somewhere safe. If you lose both, nobody can open your vaults: not even an admin, who can't reset your password either.

## Who sees what

- Other users can't see your vaults at all.
- An admin sees your username, how many vaults you have and how much space you use: never a vault's name, a note's name or a note.

## Keys and connections

An API key or a connection opens exactly the vaults, with exactly the rights, that you gave it, and stops the moment you revoke it on the Access page. Everything it writes carries its name.

## The honest limits

- The running server works with your notes in memory; that is how search works. So whoever operates your Kumbu² server is trusted: choose who runs it.
- Your AI's provider sees what your AI reads and writes, under the provider's own terms. This is not end-to-end encryption.
- Search by meaning runs on the server itself, unless your server offers an external provider and you choose it for a vault (then the vault's ⋯ menu → **Search by meaning…**). That provider then receives the vault's text.

## Deleting is for good

Deleting a vault or your whole account (in Settings, with your password) can't be undone. Backups your operator keeps are not reached by a deletion: ask your operator about their backup policy.

## In your browser

The web app keeps no notes in your browser's storage.

More on taking your notes with you: [[Your data]]. Back to [[Start here]].
