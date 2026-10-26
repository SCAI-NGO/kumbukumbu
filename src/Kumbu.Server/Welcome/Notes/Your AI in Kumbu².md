# Your AI in Kumbu²

## How it gets in

Your AI reaches Kumbu² through MCP, the Model Context Protocol: an open standard for giving an AI assistant tools to work with. It gets exactly the vaults you give it, either to read or to read and write. How to set it up: [[Connect your AI]].

## What it can do

- list and read notes, search them by words and by meaning, follow links and backlinks;
- look at a note's history and at what changed between two versions;
- write and edit notes, or just one section of a note;
- move and rename notes and folders (links to them are rewritten);
- remove notes to the trash;
- make checkpoints;
- replace a text across a folder, after showing you a preview first.

## It can't overwrite your work by accident

Every write names the version your AI read. If the note changed in the meantime, by you or anyone else, the write fails, and your AI reads the note again before it tries once more.

## Seeing what it did

- Its writes carry the name of its key or connection, so you see which of your agents wrote what.
- When you open a note, a banner shows what your agents changed since your last visit, with the changes highlighted.
- While a note is open, a bar tells you that it was changed elsewhere.
- The vault's ⋯ menu → **Activity** lists the changes in a vault, grouped by session, each with a side-by-side comparison.

What you change in Obsidian counts as yours, not as an agent's.

## Undoing

- Every note has its history: the note's ⋯ menu → **History**, then restore an earlier version.
- Before a big job, a checkpoint lets you go back in one step. "Make a checkpoint of this vault first" is a good sentence to give your AI.

## Taking access away

Open the drawer → Access, and revoke the key or the connection. It stops working at once.

Back to [[Start here]].
