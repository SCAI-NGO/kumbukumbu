# Obsidian

Kumbu² Sync keeps an Obsidian vault and a Kumbu² vault the same, on a laptop or on a phone. It syncs text notes only: images and PDFs stay on your device, and are counted as "not synced".

## Connecting a vault, step by step

1. In Kumbu², open the vault's ⋯ menu → **Connect Obsidian…**. It shows the server address, {{server}}, and a pairing code.
2. In Obsidian, install the community plugin **BRAT**, add the beta plugin `SCAI-NGO/kumbu-obsidian` with it, and enable **Kumbu² Sync**.
3. In Kumbu² Sync's settings, enter the server address, a name for this device and the pairing code. The code works once, for 10 minutes.

## The first sync

The first sync merges both sides. A note on only one side is copied to the other; notes that are the same on both sides are left alone.

Only if some notes differ, you get one question for all of them:

- **Keep both** (safest): your Obsidian text becomes a copy next to the original.
- **Keep local**: your Obsidian version wins.
- **Keep Kumbu²**: the Kumbu² version wins; your local version goes to Obsidian's trash first.

At the end it says how many notes moved, and "no conflicts" when there were none.

## After that

While you are online, changes arrive within seconds; otherwise on the next start. If both sides changed the same note, a conflict copy appears next to it, with a line at the top saying where each change came from. Merge what you need and delete the copy.

## Each device has its own key

Every device gets a key of its own, named `<vault> – Obsidian <device>`, listed on the Access page (drawer → Access). Revoking it stops that device. By default it doesn't expire; the Connect Obsidian… dialog lets you choose 30 days, 90 days or 1 year instead.

Back to [[Start here]].
