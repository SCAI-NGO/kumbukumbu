# AI links for published notes, and the publishing switch for skill link copies (0.5.0)

Status: design, agreed with Chris on 2026-11-11. Amends the publish design (`2026-11-05-publish-design.md`) where §6 says so.

## 1. Why

A published note is meant to be read, and today more and more of its readers read through an AI. The moment that started this: Chris worked something out with his Claude, published it, and wanted to tell a colleague "have a look at this with your Claude". It failed, or nearly: the colleague's Claude had to work its way to the text with `curl` (a `POST` with the code and an `Origin` header), since a fetch tool never sends what follows `#`, and the reader page needs scripts. 0.4.0 added Copy as Markdown and a hint for agents in the page's `<noscript>` (#183) as a stopgap.

What should happen instead: the recipient opens the ordinary link in the browser, presses one button, and gets a link their AI can simply fetch.

**The agency stays with the recipient.** The sender shares the ordinary link and nothing else; whether and how the recipient reads it with an AI is theirs to decide, and none of the sender's business. This is the principle of cognitive autonomy that Second Circuit – Association for digital freedom of thought, which owns Kumbu², stands for, met here as a design question (Chris). So the button is on the reader page, not in the owner's publish dialog.

A later use the format should suit, not part of this work: Markdown as an export format, for example for an SMTP MCP that sends a note as an e-mail.

## 2. Goals, non-goals, anti-goals

**Goals**

- On the reader page, one press gives a link an AI's fetch tool can read with a plain `GET`: the published copy as Markdown, its images reachable the same way.
- The link is short-lived (one hour) and dies at once with the publication.
- The ordinary link and everything that protects it stay as they are.
- A skill link's copy answers honestly while publishing is switched off (L-02 of the 0.4.0 audit, §5).

**Non-goals**

- One link for both, humans and AIs. It can't be done without moving the code out of the fragment (§3.1).
- A long-lived link to keep in an agent's configuration or a bookmark.
- An owner's "link for AIs" in the publish dialog or in `sh publish`: the owner can use the reader page like anyone.
- The export format for e-mail and other tools (later).

**Anti-goals**: it fails, even if it works, if

- the ordinary link's code ever reaches a server log, a referrer or a link preview;
- an AI link outlives the publication, the owner's account or its hour;
- an AI link reaches anything the reader page doesn't show (another note, a file not in the copy, metadata the copy has stripped);
- a reader is tracked: who fetched, from where, how often beyond what the read limit needs for that hour;
- the reader page becomes a way into the web app (the publish design's anti-goal, unchanged).

## 3. Decisions

### 3.1 Why a second link

What follows `#` stays in the browser: no fetch tool sends it, Claude's included. That is why the ordinary link keeps its code there: it reaches no server log, no referrer, no link preview. A fetch of the ordinary link gets the page without the code, and the server can't know which note is meant; a "for LLMs, click here" on that page can't know it either. The only way to serve the same link to an AI is to put the code in the path, which is the hole the fragment exists to close. So the AI gets a link of its own, made by someone who has the code: the reader, from the open reader page.

### 3.2 The AI link

- **Made on the reader page** by a button next to Copy as Markdown, labelled along the lines of "Link for your AI". A press shows the link with **Copy**, and one sentence: valid for an hour, anyone with it can read this note until then; for an AI to fetch, not to post in a chat (§3.6).
- **Each press makes a new link**, with a token of its own. Nothing is reused.
- **One hour**, from the press. Chris: long enough for the conversation it is pasted into; if the call or the fire drill in between took longer, press again. Two transient things, so a transient link: the AI fetches the content into its context once, and nobody files the link away.
- **A time limit, not a read limit.** A link that works once breaks in practice: fetch tools retry, an AI fetches again later in the same conversation (after its context was summarised), and a link pasted into a chat by mistake is fetched by the preview first. So: as many reads as needed within the hour, with a ceiling against abuse of **50 requests plus 5 per image of the copy**, the Markdown and its images together, so a copy with many images can still be read in full a few times *(Claude's call; a copy has no limit on its number of images, only on its bytes)*. Past the ceiling the token is dead, as if expired.
- **Dies with the publication.** Withdrawn, the note deleted or its vault, the owner deactivated or deleted: the token answers as an expired one does (§3.5). An **Update** of the copy keeps the token alive, and it then serves the updated copy, as the reader page does after a reload *(Claude's call: the token names the publication, not one snapshot; an Update keeps the publication's document, key and lookup)*.
- **Publishing switched off pauses the token, as it pauses the publication.** While off, the token gives the off sentence (§3.5); switched on again within its hour, it works again, as the publication does. Nothing is swept at startup *(Claude's call: the same rule as the publication it opens, and it expires within the hour anyway)*.
- Only someone holding the reader cookie of that publication can make one: the press goes to the reader endpoint with the cookie and the copy id, as the page's file requests do. No account is needed, as for reading.

### 3.3 What the AI gets

- `GET <base>/s/t/<token>`: `200`, `Content-Type: text/markdown; charset=utf-8`. The copy as the reader page renders it: the title as a level-1 heading, one line saying what this is ("A published note, snapshot of <date>; links to other notes are plain text."), a blank line, then the filtered Markdown exactly as stored. Not HTML, no script, no styles.
- **Images**: each image the copy holds is referenced in the Markdown by an absolute URL under the same token, `<base>/s/t/<token>/files/<index>`, which serves exactly what `GET /api/s/files/{index}` serves the page (metadata already stripped when the copy was made). Beside each, in the Markdown, its media type, so an AI whose tool can't show images still knows something was there. Chris asked what an AI reader needs; Claude's answer: without the pictures a text that says "see the figure" misleads, and links cost nothing when unused.
- Only what the copy holds, nothing else of the vault. PDFs are not published today (the reader endpoint serves images only, decision 1); should they be, they come the same way.
- Headers as the reader page's (publish design §3.12): `X-Robots-Tag: noindex, nofollow`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, no cookies set, no CORS. A file is served with its image type and nothing else, as `GET /api/s/files/{index}` does today (no `Content-Disposition`).

### 3.4 The token

- 160 random bits as 32 Crockford Base32 characters, without hyphens in the URL. It is **in the path**, by design: that is what makes it fetchable. It therefore may reach a proxy's access log or a chat preview; it is short-lived and opens one copy for an hour, which is the accepted cost (§6).
- The database never holds the token: it holds a lookup id derived from it (HKDF, as the share code's), the publication it opens, the publication key sealed under a key derived from the token (the reader key's way, publish design §4), its expiry and its request count. The token alone opens the copy; a database dump without it opens nothing.
- Expired tokens are removed by a TTL index; a withdrawal or deletion removes the publication's tokens with it (vault and account deletion: deleted by `vault` in `DeleteVaultsAsync`, in the transaction and the sweep after it, and in `DeleteCopiesOfVaultsAsync`; the collection is **not** in `ContentCollections`, so tokens readers make never count towards the owner's storage, and it holds nothing sealed under the vault). *Amended 2026-11-12 after the overnighter, which followed the brief here: Chris: the space is surely tiny, but it should be correct.*
- **The server never logs the token**, its lookup id or the `/s/t/…` path's last segments, and leaves `/s/t/` out of the HTTP metrics' route labels, as it does for the reader paths (`ReaderPaths`).

### 3.5 Answers

- A malformed, unknown, expired or used-up token, a withdrawn publication, an inactive owner: one `404` with one sentence, plain text, the same for all ("This link for AIs has expired or the note is no longer published. Ask for a new one on the note's page."). As the reader endpoint does, no answer tells them apart.
- Publishing switched off: the honest fact about the server, as the reader endpoint's `publishing-off` answer is ("Published notes can't be read on this server."), plain text. A fact about the server, not about the link.
- Making a link while publishing is off, or without a valid reader cookie: the reader endpoint's existing answers.
- **Rate limits** *(Claude's calls)*:
  - Making a link counts as a read of the reader endpoint, with its per-address, per-network and server-wide limits (`AccountLimits`, publish design §3.12). No cap per reader cookie: the cookie holds the share code, which every reader of the note shares, so such a cap would be one per note that one reader could use up for all; the anti-goals forbid a per-reader identity.
  - Fetching the Markdown counts as a copy read (`ReaderRead`), an image as a file read (`ReaderFileRead`), with the same three limits; and against the token's ceiling.
  - An unknown, expired or used-up token does **not** count as a wrong code (`ReaderFailed`/`ReadersBlocked`). With 160 random bits counting catches no guesser, while AI tools reach the server from a provider's shared addresses: a tool retrying an expired link would otherwise block a whole network's reads of published notes. The ordinary limits above still bound the load.

### 3.6 Link previews

The publish design's anti-goal "a chat service's link preview shows any of the content" holds for the ordinary link. An AI link shows its content to whatever fetches it within the hour, a preview bot included; that is what it is for. The reader page says so beside the link ("for an AI to fetch, not to post in a chat"), and the token is short-lived. Recognising preview bots by their user agent is not attempted: it is guesswork, and a wrong guess hides the note from an AI. The publish design is amended accordingly (§6).

### 3.7 What stays

- The ordinary link, its fragment, the reader cookie, the reader page's CSP and isolation.
- Copy as Markdown, and the `<noscript>` hint for agents, which gains one sentence: a person with the link can make a link for AIs on the page.
- No reading statistics: the request count is kept only to enforce the ceiling and goes with the token.

## 4. The web app

- `web/src/reader/Reader.tsx`: the button beside Copy as Markdown; the link in a read-only field with Copy, the sentence of §3.2, the expiry as a time ("until 21:40"). Pressing again replaces it. On a phone the same, the field wrapping.
- The reader page's own entry and CSP stay; the new request goes to the reader endpoint like the others.
- Wording in British English, as everything on the page.

## 5. L-02: a skill link's copy while publishing is switched off

From the 0.4.0 audit (report §6, L-02; open item of 2026-11-11). A copy added from a skill link checks its link for updates (`SkillUpdates.LinkHashAsync`), and Review and Take read the link's snapshot (`SkillUpdateTaker.UpstreamAsync`). The audit's fix ended both when the owner is deactivated ("source gone", as after a withdrawal). While publishing is switched off (`KUMBU_PUBLISH_ENABLED=false`) both still read the link, against security-architecture §4 "Skills" (`KUMBU_PUBLISH_ENABLED=false` stops sharing and reading them).

**Decided by Chris: an honest answer of its own.** While publishing is off:

- The update check reports neither an update nor "source gone": it reports that publishing is switched off on this server, and the copy's stored update state is left as it is.
- Review and Take refuse with "Publishing is switched off on this server, so updates from links can't be checked right now." (a type of its own, as the reader's `publishing-off`).
- The Skills screen shows that sentence where it would show the update state.
- Switched on again, everything continues as before; nothing was lost.

**Why** (Chris): honesty is part of the security architecture. Answering "source gone" when the source isn't gone would be dishonest, close to security by obscurity, and a user might detach a copy that will work again tomorrow. Leaving it as it is would be one of the exceptions that end with a product "not quite as consistent as promised". The skills design §3.10 and security-architecture §4 "Skills" get the rule.

## 5a. End to end

1. Alice publishes `/Work/Pump/Ruling.md`, which embeds two images, and sends Bob the ordinary link `https://kumbu.example.org/s#K7QM-4XDA-…` in a chat. The chat's preview shows nothing of the content, as before.
2. Bob opens it in his browser. The reader page shows the note, Copy as Markdown and, beside it, **Link for your AI**.
3. Bob presses it. The page shows `https://kumbu.example.org/s/t/3XQ9…` (32 characters after `/s/t/`), "valid until 21:40", and the sentence of §3.2. He copies it into a conversation with his own Claude: "Read this and tell me what you think of the ruling."
4. Bob's Claude fetches the link with a plain `GET`: `200`, `text/markdown`, the title as a heading, the "published note, snapshot of …" line, the filtered text, and two image links `…/s/t/3XQ9…/files/0` and `…/files/1` with their media types. It fetches the first image as well, and answers.
5. Half an hour later Bob asks a follow-up and Claude fetches the link again: still `200`.
6. Alice withdraws the publication. Bob's Claude, fetching again: `404`, "This link for AIs has expired or the note is no longer published. Ask for a new one on the note's page." So does every image URL. The ordinary link is dead too, as before.
7. Had Alice not withdrawn it, the link would answer the same `404` from 21:40 on; Bob, back from a phone call, opens the reader page again (its cookie still works) and presses the button for a new link.

## 5b. Out of scope

- Anything about the ordinary link, the share code, the reader cookie, the reader page's CSP and isolation, beyond the new button and its request.
- An owner's AI link (publish dialog, `sh publish`, MCP); a long-lived or reusable AI link; a read-once link.
- PDFs in published copies (not published today).
- Markdown as an export format for e-mail or other tools.
- Recognising link-preview bots.
- The rest of the skills' update logic beyond §5's publishing-off answer; the deactivated-owner rule of L-02 stays as the audit fixed it.
- Everything else in `docs/superpowers/open-items.md`.

## 5c. Security boundary

- **New:** the token endpoints under `/s/t/` (anonymous, rate-limited, the reader's headers), the token documents and their key derivation, and the request that makes a token from the reader endpoint. These need the security review of §7 and the documentation of §6.
- **Not touched:** authentication and sessions (`Auth/`, `CallerMiddleware`), vault keys and the slot model (`Crypto/`), the share code's derivation and the reader cookie (`ShareCode`, `ReaderApi`'s existing endpoints, apart from adding the new request beside them), the publication documents' format, the fair copy (`Kumbu.Core/Publishing/FairCopies.cs`), WebDAV, OAuth, the MCP surface.
- The token never opens more than the publication's copy and its images; it never reaches the vault, its key, its record, or another publication.

## 6. Amendments and documentation

- **Publish design** (`2026-11-05-publish-design.md`): §2's anti-goal on link previews gains "(of the ordinary link; an AI link, made by a reader on purpose, shows its content to whatever fetches it within its hour: §3.6 of the AI link design)"; §3.12 gains a pointer to this design. A short dated note at the top names the amendment.
- **security-architecture.md**: the threat table gains "someone holding an AI link" (reads that one copy and its images for at most an hour; nothing else; dies with the publication); §5.6 "What is in the clear" gains the token documents (lookup id, vault, publication, expiry, count); §7 the new endpoints, their limits and headers, and that the token is in the path by design and may reach a proxy's access log; §4 "Skills" the L-02 rule.
- **README**: "Publishing a note" → "For an AI" describes the button; the skills section the publishing-off sentence.
- **concept.md**: the publishing paragraph, one sentence.
- `CHANGELOG.md` at the release, from the version diary.

## 7. Tests (behaviour, each red before)

- A token made from a valid reader cookie returns the copy as `text/markdown` with the title line and the filtered text; its image URLs return the images the page gets, and nothing else (another index: 404).
- After an hour (clock moved), past the ceiling (50 plus 5 per image), after Withdraw, after the owner's deactivation, after the vault's deletion: the one 404 sentence. After Update: the new copy.
- Publishing off: the off sentence, for an existing token and for making one; switched on again within the hour, the existing token works again.
- Unknown and expired tokens, many of them from one address: no `ReadersBlocked` for that network.
- No cookie, another publication's cookie, a wrong copy id: no link.
- The token, its lookup id and the `/s/t/` segments appear in no log line and no metric label (a log capture over a full round).
- A database read of the token documents shows neither the token nor anything that opens the copy without it.
- Headers of §3.3 on the Markdown and on a file.
- L-02: with publishing off, the update check keeps the stored state and says publishing is off; Review and Take refuse with the sentence; switched on, an update offered before is offered again.
- Web: the button, the link with its expiry, Copy, a second press replacing it; the page's CSP unchanged.

## 8. Left to the builder

- The exact Base32 alphabet handling and the HKDF labels (follow `ShareCode`).
- Whether the token documents live in the publications' collection or one of their own (mind `ContentCollections`).
- How the Markdown's image references are rewritten (the stored copy names its files by index).
- Small wording on the page within the meaning of §3.2 and §3.6.
- The counting of the requests (atomic increment on the token document is enough).
- Sealing the owner's id beside the key (for the deactivation check), the `vault` field that lets `ContentCollections` deletion reach the tokens, the hooks in `DeleteCopyAsync` and `DeleteCopiesOfVaultsAsync`, `/s/t/` in `ReaderPaths` (today it falls through to `index.html`), and how the publishing-off state of §5 appears in `SkillUpdateState`.
