# Test playbook: vault sharing, by hand and with an agent

A test of vault sharing (spec [2026-10-31-collab-design](superpowers/specs/2026-10-31-collab-design.md)) on a real instance, the way people will meet it: new accounts, invitation links, two or three browsers side by side, and an agent on a member's key. The automated tests cover the rules; this one looks for what only people notice: unclear dialogs, wrong names on the screen, things that don't update.

For each step, tick it or note what differed. Note also every moment you hesitated, even if the step worked: that is the most valuable part of the result. Expected sentences are from the design; a different wording is fine if it says the same.

## Before you start

- The instance runs `0.3.0-pre.2` or newer, with test data only.
- Three browser profiles or private windows, signed in separately. Private windows of a Firefox-based browser share one session, so there use separate profiles, or different browsers. **Alice** is an existing account with admin rights. **Bob** and **Carol** are made in part A.
- The sidebar's vault menu is the vault's ⋯ menu in the drawer. Vaults are switched in the popout at the bottom of the sidebar ("Switch vault: <name>").

Already known, so no need to note them (overnighter report, section 8):

- A link inside a note shows the owner's name for the vault, and is dead for a member whose name for it differs.
- "Bob Example (username bob)" can show the username twice.
- A member who deletes their own account gets no receipt.
- Refusal sentences can carry literal `*` around a vault's name.
- Plugin 0.2.0 isn't out yet; step 37 expects the refusal.

## A. New accounts

1. As Alice: Admin, Invitations, make an invitation for a user. *Expected:* a link, shown once.
2. In Bob's browser: open the link, choose a username and a password, note the recovery key. *Expected:* signed in, with a vault *Welcome*.
3. The same for Carol.
4. As Bob: make a vault named `Gartenarbeit`. It will collide in step 11.

## B. Sharing a vault

5. As Alice: make a vault `Gartenarbeit`, with a note `plan.md`, a note `early.md` and a board. Vault menu, **Share…**. *Expected:* the access list, empty, with a way to invite.
6. Invite: who it is for "Bob", role read and write, default expiry, no confirmation. Password needed. *Expected:* the sentences that members with write can change and delete, and that new members see everything, also what was written before they joined. The link appears once, with a copy button.
7. Close the dialog and look at the access list. *Expected:* Bob as pending. The link can't be shown again.
8. Invite "Mallory", then withdraw it. Open that link in Bob's browser. *Expected:* a plain refusal that doesn't say whether the link ever existed.

## C. Accepting

9. Sign Bob out and open his link. *Expected:* it says an account comes first and offers sign-in. After signing in, Bob is back at the invitation, and the address bar and the page's address hold no secret.
10. *Expected on the page:* vault, owner with username as its own field, role, how the vault is searched, and until when the link works. The owner's name and username are enough to tell who this is.
11. *Expected:* the field "Your name for this vault" says Bob has `Gartenarbeit` already and asks for another. Choose `Garten Alice`. Accept. *Expected:* "You can open it now".
12. *Expected:* Bob's vault popout lists "Your vaults" and "Shared with me" by owner, with `Garten Alice` under Alice, then "New vault" and "All vaults". As Alice, the access list shows Bob as active.
13. Open the used link again as Bob and as Carol. *Expected:* the same plain refusal as in step 8.
14. As Alice, invite "Carol" as read only, with **Ask me to confirm**. Accept as Carol. *Expected:* Carol is told the owner confirms first, and the vault doesn't show yet.
15. As Alice: *Expected:* who opened the link, by name and username, with a choice. Decline. As Carol: nothing gained. Invite again, confirm with the password. As Carol: the vault shows up, in an open tab too.
16. As Bob: share his own vault `Gartenarbeit` and open that link himself. *Expected:* refused, since it is his own vault. Withdraw the invitation.

## D. What each level can do

Bob has write and Carol read.

17. As Carol: open `plan.md`. *Expected:* reads it, also `early.md` from before she joined. Write mode isn't offered, or says why. No uploading, renaming, deleting.
18. As Bob: edit `plan.md`, add a note, upload an image, delete a note and restore it from the trash. *Expected:* all work.
19. As Bob: on the board, add and move a card. *Expected:* works. Rename a column, and open `Board/Board.md` in Write mode. *Expected:* refused, with the sentence that only those who may maintain can change the board's columns.
20. As Bob: make a checkpoint. *Expected:* refused for the same reason.
21. As Alice: raise Bob to maintain. *Expected:* the password is asked, every time, even within the password window, and maintain stands in the warning colour. As Bob, without reloading: renaming the column and the checkpoint now work.
22. As Alice: lower Bob to write. *Expected:* no password, lowering never asks. As Bob: the next attempt is refused, and the vault's activity says Alice changed his role, among the other rows and in time order. In Settings, "Shared vaults", Bob's own events show it too.
23. As a member: vault menu. *Expected:* **Shared with you…** with role, owner and provider; no Share, no access list, no member list, no re-encrypt, no delete.
24. As Bob: rename `Garten Alice` to `Beet`. *Expected:* only Bob's name changes. Alice and Carol keep theirs.

## E. Names and live updates

25. Open `plan.md` as Alice, Bob and Carol, side by side. As Bob, go to Write mode. *Expected:* Alice sees "Bob is editing this note"; Carol sees "another user", and never Bob's name.
26. As Bob, change the note and save. *Expected:* within seconds the others are told, without reloading. In Write mode, Alice's text is never replaced by itself.
27. As Alice and Bob at once: edit the same note. *Expected:* the second to save gets the conflict prompt, which names the writer as in step 25.
28. History and activity, viewed by each. *Expected:* Alice sees "Bob"; Bob sees "You" and Alice's name; Carol sees Alice's name and "another user". Look for Bob's username or his key's name anywhere on Carol's side.
29. As Bob: export the vault as ZIP. *Expected:* the password is asked. As Alice: the activity says Bob exported it.

## F. An agent on a member's key

As Bob: Access, make an API key named `Bob's Claude` with `maintain` on `Beet` (role write). If Bob's key covers both `Beet` and a vault of his own, *expected:* the warning that an agent reaching both could be talked into copying between them. Note how it reads. The key page should show the key as capped by the role.

Connect it in a fresh Claude Code session in an empty folder, and paste the block below the line (with `<url>` and `<key>` filled in):

```sh
claude mcp add --transport http kumbu-bob <url>/mcp --header "Authorization: Bearer <key>"
```

Give the agent time to finish before the next step.

30. After the agent's part C, as Alice: lower Bob to read. Then tell the agent: "Read `/Beet/plan.md` twice, and report anything unusual in both results." *Expected:* the first result carries a one-time notice of the new role, the second doesn't.
31. Tell the agent: "Add a line to `/Beet/plan.md`." *Expected:* refused, and the agent says why.
32. As Alice: raise Bob back to write.

## G. Ending it

33. As Alice: revoke Carol, with the re-encrypt box off. *Expected:* an open tab of Carol's loses the vault at once, without a reload, and she gets a sentence, not a crash. Carol's old links to its notes lead nowhere.
34. As Bob: **Leave this vault**, confirmed. *Expected:* gone from Bob's list; Bob's key loses the vault, and is revoked if it has no vault left. As Alice: the activity says Bob left, among the rows and in time order, and offers re-encryption with an honest sentence. In Settings, "Shared vaults", Bob still sees his own events for this vault, including that he left. Tell the agent to `ls /`. *Expected:* `Beet` is gone.
35. As Alice: invite Bob again and accept. Then re-encrypt the vault. *Expected:* during the run Bob sees "being re-encrypted"; after it he can read again, and is told to give his agents the vault again. An invitation still open at the start is withdrawn, and Alice is told.
36. As Carol: make a vault `Carols Notizen` with a note, and share it with Bob as write. As Alice, in Admin: deactivate Carol. *Expected as Bob:* a banner that the vault is read-only until her account is active again, and export still works. Reactivate Carol. *Expected:* writing works on the next request.
37. As Bob, pair Obsidian with plugin 0.1.x to the shared vault. *Expected:* "Update the Kumbu² plugin to 0.2.0 or newer to sync a shared vault." An Obsidian already paired to one of Alice's own vaults goes on syncing.
38. As Carol: delete her account. *Expected:* the dialog lists `Carols Notizen` and offers two choices, "Give members 7 days" (the default) or "Delete it for everyone now". Take the 7 days. *Expected as Bob:* a banner with the date, read only, and export works. As Alice in Admin, delete Bob's account with a privacy request; the admin's dialog offers the same two choices for vaults he owns and shares. *Expected:* the receipt counts what ended. Old entries in Alice's vault show "a former member" and nowhere Bob's name.
39. Dashboard: the Sharing row counts invitations and membership changes.

---

You are testing vault sharing in Kumbu², a Markdown second brain, through the connector `kumbu-bob`. You act for Bob, who is a member of a vault he calls `Beet`; someone else owns it. His role is write, and this key was set to maintain. Use only what the tool descriptions, `sh help` and the tools' answers tell you. Change nothing outside `/Beet/agent-test/`, except where a step says so. Stop after part C and wait.

For each step, say what you called, what came back, and **PASS**, **FAIL** or **UNCLEAR** (the tools left you guessing). Note every retry or guess, even if the step worked.

### A. Finding your way

1. `ls /`. Which of these vaults are Bob's own, and which shared? Can you tell from the answer, and whose it is?
2. `uname -a`.
3. `rg` for a word in `/Beet/plan.md`, and `semsearch` for its topic. *Expected:* both find it under the name `Beet`, never another name for the vault.

### B. Working

4. Write `/Beet/agent-test/note.md`, then edit it. *Expected:* works.
5. `history /Beet/agent-test/note.md` and `history /Beet`. *Expected:* your own writes are marked as yours. The owner has her name. Other members are "another user", with no name, key or device.
6. `board /Beet`, and add a card to the first column. *Expected:* works.
7. Rename a column, and edit `/Board/Board.md`. *Expected:* both refused. Your key is set to maintain, but the role is write: does the sentence make that clear?
8. `mkcp /Beet agent-test`. *Expected:* refused for the same reason.

### C. Your view

9. Did anything tell you that this vault is shared, and that others can read what you write? Would you have written differently if you had missed it?
10. What was unclear, and what would you change first? Quote the sentence as you would write it.
