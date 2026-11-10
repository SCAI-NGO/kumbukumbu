# The findings of the pre.4 tests

Date: 2026-11-10. Status: approved by the maintainer on 2026-11-10.

On the morning of 2026-11-10 Claude tested `v0.4.0-pre.4` on staging through Playwright, with the test users `claude4-alice`, `-bob` and `-carol`. F1 to F4 of the pre.3 tests passed. Step 2.9 of the test list ("Time-out while typing") passed online, but its offline variant found two faults that block 0.4.0, and two small things came up on the way. The test list lives in the maintainer's Kumbu² (`/SWE/Kumbu/Tests/Test list 0.4.0-pre.3.md`, findings F7 to F10); this spec takes them for 0.4.0.

One branch, one pull request, each section its own commit. Order by weight: §2 F9, §3 F10, §4 F7, §5 F8.

## 1. Goals, non-goals, anti-goals

Goals: §2 to §5.

Non-goals:

- Any change to what discreet vaults hide, the PIN check, the server's late-save rule (one late save of the note that was open, within 5 minutes of the end; discreet-vaults design §3.8) or its clock.
- A held draft that misses the late-save window: it is forgotten, as today. Offline for longer than 5 minutes past the end loses that text, by design.
- F5 (a stale request after the rename), which #182 handled, and anything else on the test list.

Anti-goals, which make this a failure even if it works:

- **Text the user typed is lost while the rules say it should be saved.** That is F9, and no fix may move the loss somewhere else (a 409 that drops the draft, a retry with an old version).
- **Anything on screen names a hidden vault, or a note in it, once the reveal has ended.** That is F10. No error text, header, title, tab title, toast or URL.
- **A fix that makes the online case worse.** Step 2.9 online passes today: typing until the end, the note closes, the app goes back to the last ordinary note, everything saved once.

### An example from end to end

Alice reveals her discreet vaults for 5 minutes and writes in `/Tagebuch/Heute.md`. Two minutes before the end her train enters a tunnel; she keeps typing. The end passes while she is offline: the note closes, the app shows `Home`, and nothing on screen says `Tagebuch` or `Heute`. Three minutes after the end the train leaves the tunnel: within a few seconds the text she typed in the tunnel is saved, exactly once, with everything up to her last word. Revealing again, she finds it all in `Heute.md`, and its history has one new version for the tunnel text. Had she come out of the tunnel only six minutes after the end, the tunnel text would be gone (the window is 5 minutes), and the screen still would name nothing.

### Security boundary

Web app and `Kumbu.Core`'s search snippet only. Not touched: the server's API and its answers, the late-save rule and window, the PIN check, the session, encryption, accounts, sharing, WebDAV, sync, the CSP. §4 changes when the web app asks, not what the server answers.

## 2. F9: text typed offline near the end of a reveal is saved

**What happened** (staging, pre.4, `claude4-alice`, Chromium through Playwright, `context.setOffline`): revealed for 5 minutes at about 07:02:15 UTC; `/Welcome/Tagebuch.md` in Write mode; typed a word every 5 s while online until 07:06:00, then offline and ` off0` … ` off33` every 3 s; online again at 07:07:51, while the pill still said "1 min". The note's history shows one save at 07:07:51; it holds everything up to ` off0`. ` off1` … ` off33` are gone. Seconds later the reveal ended and F10 showed.

**What should happen:** while the browser is offline, what the user types stays in the editor and is saved when the browser is online again: the latest text, once, on the version the server has. This holds before the end of a reveal (the editor's ordinary save), across the end (the held draft of `web/src/discreet/hideAgain.ts`, sent within the late-save window) and when the end comes while a save is in flight.

**Find the cause first.** Before any fix, list every possible cause, ranked by likelihood, and confirm or rule out each with a failing test. Candidates seen from the code, not yet checked:

1. The editor's save that failed offline is retried with the body it had then (` off0`), not the latest text, and later edits are not scheduled while that save is pending.
2. After the stale retry succeeds, the editor still holds the old version: the next save (the latest text) gets a 409, and the draft is dropped or held with the old version, so the late save gets a 409 as well.
3. `saveAhead` (15 s before the end) and the end's `hideAgain` run while offline: `saveOpen` fails, `hold` takes `unsaved()`, which may be the failed save's text rather than the editor's current text.
4. The held draft's retry (`online` event) and the editor's own retry both fire when the browser is online again; one wins with old text, the other loses with a 409 or a 404 (the reveal has ended meanwhile, the session read says hidden).
5. The reveal ended between the two saves: the second is no late save in the server's eyes (§3.8 takes one), and gets 404.

Whatever the cause, the test that proves the fix is the example above with fake timers and a simulated offline browser, in the web app's tests: the latest text reaches the server once, with the server's current version. Also with the end coming while offline (held draft) and with the end coming while a save is in flight.

## 3. F10: after the end, nothing on screen names the discreet note

**What happened** (same run): seconds after the browser was online again, the reveal ended. The app stayed at `/discreet`; the sidebar showed `Home`, but the header said "Welcome / Tagebuch", and the note's place said "Tagebuch" and "There is no note at /Welcome/Tagebuch.md (if it was removed, it is in the trash)." The online run of 2.9 closed cleanly; the difference is a save or a read of the discreet note answered 404 around the end. Screenshot on the maintainer's laptop (`~/scratchpad/playwright/discreet-offline-after.png`).

**What should happen:** when a reveal ends, by the clock, by the server's session read, by a `hidden` frame or by ✕, the screen is as in the online case: the discreet note is closed, the app shows the last ordinary note the tab read or the vault overview (F6's rule), and nothing names a hidden vault or a note in it. This holds whatever answers around the end: a 404 for the note, its links, backlinks, version or publication, a 409, a failed save, an offline browser. A 404 for a path in a vault that was discreet in this tab never renders as "There is no note at …" or any other text with the path; while hiding or after it, such an answer is treated as "the note went away with the vaults".

**Find the cause first,** as in §2. Candidates:

1. A query of the discreet note (`note`, `links`, `backlinks`, `version`) refetches when the browser is online again, gets 404 because the server's reveal has ended, and the note view renders the 404 before or instead of `hideAgain`'s navigation.
2. `hideAgain` navigates only when `vaultOfRoute(pathname)` names a discreet vault or `showsDiscreet()` holds; around an offline period one of them may be false while the note view still shows the discreet note (the header and the error are the editor's `currentPath`, not the route).
3. The 404 listener in `useDiscreetMode` invalidates the session; `followSession` calls `hideAgain` while a first `hideAgain` (the clock's) is running, joins it, and the first one decided where to go before the 404 changed the screen.
4. `forgetVault` clears the editor's state for the discreet vaults, but a view that already rendered the error keeps its text until it unmounts, and it doesn't (same `key`).

The test: the example above, with the end coming while offline and while a save is in flight, and with the 404 arriving before, during and after `hideAgain`: afterwards the document's text contains neither the vault's name nor the note's.

## 4. F7: no 404 in the console for an unpublished note

Opening a note asks `GET /api/publications/{vault}/{path}` (`usePublication`, `web/src/api/publications.ts:44-61`); for a note that isn't published the answer is 404, which the browser logs as an error each time. Only noise, but it hides real errors when somebody looks.

- The web app asks that endpoint only for a note it knows to be published: the vault's list of publications (`GET /api/vaults/{vault}/publications`, `usePublications`) names them, for the owner and for members alike. A note not in that list shows no publication bar and makes no request. When the vault's list isn't loaded yet, wait for it rather than asking per note.
- Everything else stays: the bar's states, Copy link, Update copy, Unpublish, what members see, publishing from the menu (after `POST`, the list is invalidated, as today), the server's answers.
- While publishing is switched off on the server, the bar of a published note still shows Unpublish, as today, gated by the vault's list as above.

## 5. F8: a search hit's snippet shows the line that matched best

`SectionSearch.Snippet(chunk, query)` (`src/Kumbu.Core/Search/SectionSearch.cs:148`) takes the first line of the chunk that holds the query or any of its words. In a table chunk nearly every row holds some word, so the snippet is the first row, not the one searched for. Searched for "Pumpe 51 Druck", the chunk of lines 47–74 of a 60-row table is the right hit, but its snippet is `| E228 | Pumpe 28 | Druck zu niedrig |`, not the row with 51.

- The snippet is the line that holds the whole query (ignoring case), if one does; otherwise the line that holds the most distinct query terms (`Bm25.QueryTerms`); the first such line among equals. No line with any term: the start of the section, as today.
- It applies wherever that overload is used: the web app's search (hybrid, meaning, exact) and the earlier-versions search. `semsearch` prints whole cores and is not affected.
- Searched for "Pumpe 51 Druck" in that table: a row with "Pumpe 51" and "Druck" wins over `E228`; where the row with 51 says "Dichtung", the row with the most terms wins, and a test pins exactly which.

## 6. Hand tests

The run writes a hand-test checklist into its report: step 2.9's offline variant as in the example (5 minutes; offline before the end, online within 5 minutes after it; then also offline longer than 5 minutes past the end, where the text is gone but the screen names nothing), the online case once more, a published and an unpublished note with the console open, and the search in the 60-row table. The maintainer and Claude run them on staging with a pre-release.
