# Test playbook: the read-only console

A test of the web app's console (spec [2026-11-02-console-design](superpowers/specs/2026-11-02-console-design.md)) on a real instance, by hand. The automated tests cover the rules: which commands are refused, the paths the console sends, what the server answers. This one looks for what only a person at a screen notices: pictures and PDFs that really arrive, a phone's keyboard, where the page scrolls to, what a copy picks up, and whether anything is left behind for the next person at the browser.

For each step, tick it or note what differed. Note also every moment you hesitated, even if the step worked: that is the most valuable part of the result. Expected sentences are from the design and the code; a different wording is fine if it says the same.

The console has two prompt lines, the working directory and then `>`. In the scrollback each command shows as `> ` and what was typed, and, for everything that went to the server, a quieter line `→ ` and what was sent. "The output" below is the scrollback; "the input" is the field after the last `>`.

## Before you start

- The instance runs a build with the console (the pull request's `:sha-…` image, or a pre-release after it), with test data only, and with the PDF sidecar running.
- Two browser profiles or private windows, signed in separately. Private windows of a Firefox-based browser share one session, so there use separate profiles, or different browsers. **Alice** owns the vaults; **Bob** is a second account.
- A phone with the instance's address, for part I.
- At hand: a photo from a phone (JPEG), a screenshot (PNG) and a PDF of a few pages with at least one chart or scanned page.

## A. Opening

1. As Alice: open the drawer, and in the account links next to Access and Settings, **Console**. *Expected:* the page "Console", with "Back to the notes" above. The output starts with one line: `Kumbu² console, not a bash, and read-only. Type help for the commands, help <command> for details.` Below the output, `/` and then `>` with the cursor in the input, ready to type without a click.
2. Switch the colour scheme in Settings to the other theme and come back. *Expected:* every line reads in both themes: output, the quieter `→` lines, errors and the path line.

## B. The agreed example

3. In the web app, make a vault `Gartenarbeit` if Alice has none, a folder `Shopping` in it and a note `/Gartenarbeit/Shopping/plan.md` whose only line is `Tomaten`. Go back to the console.
4. Type `cd /Gartenarbeit/Shopping`, Enter. *Expected:* no output, and the path line above the input reads `/Gartenarbeit/Shopping`, with `/Gartenarbeit` in the accent colour and `/Shopping` muted. The path is readable without the colours too.
5. Type `rg -n Tomaten plan.md`. *Expected:*

   ```
   /Gartenarbeit/Shopping
   > rg -n Tomaten plan.md
   → rg -n Tomaten /Gartenarbeit/Shopping/plan.md
   /Gartenarbeit/Shopping/plan.md:1:Tomaten
   ```

   The path line is shown once, above the command, since the directory changed. Compare the `>` line and the `→` line: the two commands start in the same column, so the difference (`plan.md` against the full path) stands out at a glance. Check this on a laptop and, in part I, on the phone; a font where `→` is wider than `>` would shift one line.
6. Select the `→` line with the mouse and copy it into a text field elsewhere. *Expected:* `rg -n Tomaten /Gartenarbeit/Shopping/plan.md`, without the `→`. The same for a `>` line: no `>` comes along.
7. Type `rg Tomaten`. *Expected:* `→ rg Tomaten /Gartenarbeit/Shopping`, and the hit. Then `rg Tomaten /`. *Expected:* the `→` line equals what was typed, and every vault was searched.
8. Type `pwd`. *Expected:* `/Gartenarbeit/Shopping`, with no `→` line: it never went to the server.

## C. Moving around

9. `cd plan.md`. *Expected:* an error line `cd: /Gartenarbeit/Shopping/plan.md: not a folder`, marked as an error by colour and by a `!` in front, and the path line stays `/Gartenarbeit/Shopping`.
10. `cd nope`. *Expected:* `cd: /Gartenarbeit/Shopping/nope: not a folder`, and the directory stays.
11. In the web app, make an empty folder `/Gartenarbeit/Leer` ("New folder…"). In the console: `cd ../Leer`. *Expected:* the path line reads `/Gartenarbeit/Leer`. `ls`. *Expected:* `→ ls /Gartenarbeit/Leer` and an empty answer, not an error.
12. `cd ..`, then `cd ..` again, then `cd ..` once more. *Expected:* `/Gartenarbeit`, then `/`, then still `/`. `cd` alone from anywhere goes to `/`. `..` alone does what `cd ..` does. `exit` goes back to the notes; opening the console again shows the directory, the scrollback and the history as they were, and `exit now` only says `exit: too many arguments`.
13. `cd a b`. *Expected:* `cd: too many arguments`. `pwd | head`. *Expected:* `pwd: can't be part of a pipeline`.
14. Copy one of these error lines into a text field. *Expected:* the sentence, possibly with `Error: ` in front (the screen-reader label), and no `!`. Note whether `Error: ` came along.

## D. Spaces and umlauts

15. In the web app, make a folder `/Gartenarbeit/Grüne Bohnen` with a note `Sorten.md` holding a line `Blauhilde`. In the console: `cd "/Gartenarbeit/Grüne Bohnen"`. *Expected:* the path line reads `/Gartenarbeit/Grüne Bohnen`, the `ü` as typed.
16. `cat Sorten.md`. *Expected:* `→ cat '/Gartenarbeit/Grüne Bohnen/Sorten.md'`, quoted because of the space, and the note's text. Copy the `→` line and run it again in the console. *Expected:* the same answer. Optionally, if an agent is connected, send the same line as its `sh` call: the same answer there too.
17. `cd /`, then `rg -n Blauhilde 'Gartenarbeit/Grüne Bohnen'`. *Expected:* the `→` line has the path absolute and quoted, and finds the line.
18. If the phone or a Mac keyboard types the `ü` decomposed (u and a combining mark), try step 15 from there. *Expected:* the same folder.

## E. Nothing writes

19. `cd /Gartenarbeit/Shopping`, then `rm plan.md`. *Expected:* `rm: not in this console, which only reads`, as an error, and a `→` line equal to what was typed.
20. `cat plan.md | rm plan.md`. *Expected:* the same sentence, and nothing of the line ran: no output of `cat` above it.
21. `rm --help`. *Expected:* the same sentence. `help rm`. *Expected:* the shell's help for `rm`, as an agent gets it.
22. Try `mv plan.md x.md`, `mkdir neu`, `mkcp /Gartenarbeit eins`, and one of your own spellings (`"rm" plan.md`, `r\m plan.md`). *Expected:* each refused with its own command name in the sentence.
23. In the web app: open `plan.md`, its history and the vault's trash and checkpoints. *Expected:* the note still holds `Tomaten`, with no new version; the trash and the checkpoints have nothing from these steps.

## F. Help

24. `help`. *Expected:* first the console's own block, starting `In this console:`, naming `cd [path] (.. alone is cd ..), pwd, clear, exit`, relative paths and `→`, and the refused commands `pdfimages, restore, mv, archive, mkdir, rm, mkcp and rmcp`; then `The manual agents get, unchanged:` and the shell's manual, which still says that all paths are absolute and names the write tools. The two are clearly apart.
25. `help cd`, `help pwd`, `help clear`, `help exit`. *Expected:* short local texts in British English. `help rg`. *Expected:* the shell's own text, with a `→` line.
26. `clear`. *Expected:* the scrollback is empty, the welcome line stays. Press Up. *Expected:* `clear` and the commands before it come back one by one, and Down walks back to an empty input. A command typed twice in a row is in the history once.

## G. Pictures and PDFs

The automated tests see the image parts, not pictures on a screen.

27. Upload the photo, the screenshot and the PDF to `/Gartenarbeit/Shopping` in the web app. In the console: `img photo.jpg` (with its real name). *Expected:* `→ img /Gartenarbeit/Shopping/photo.jpg`, a line naming the file, its version, the original and the delivered size, then the picture, the right way up, no wider than the output.
28. `img screenshot.png --size large`, then `img screenshot.png --crop 0,0,400,300`. *Expected:* the larger step and the crop, each as a picture, with its line of text.
29. `pdf info` with the PDF's name, then `pdf` with it. *Expected:* the page count and types, then the text page by page. Then `pdf --images` with the PDF's name. *Expected:* the chart or scanned pages as pictures between the pages' text.
30. On the host, in the instance's folder (where its `compose.yaml` is), stop the sidecar: `docker compose stop pdf`. In the console: `img photo.jpg` (the photo's real name). *Expected:* an error saying that image rendering is unavailable right now and to try again later (`image rendering is unavailable right now; try again later`). Then `pdf info` with the name of a PDF uploaded after the stop. *Expected:* an error saying that PDF analysis is unavailable right now (`PDF analysis is unavailable right now; try again later`); `pdf info` on the PDF of step 29 still answers, from its stored analysis. Start the sidecar again: `docker compose start pdf`.

## H. Scrolling and the mouse

31. Type `help` three or four times, so the output is taller than the screen. *Expected:* after each answer, the newest one's end and the input are visible, without scrolling by hand.
32. `img photo.jpg` once more. *Expected:* after the picture has loaded and pushed the page longer, the end of the picture and the input are still visible, not the middle of the picture.
33. Type `semsearch Tomaten /` (or another slow command), and while it runs, scroll up in the output. *Expected:* when the answer comes, the page stays where you scrolled to. The next command you type brings you back to the end. If every command answers before you can scroll, try it in a vault with many notes; failing that, skip the step and note it.
34. Click into the output, on no particular line. *Expected:* the cursor is back in the input; typing goes on. Now select a few words of the output with the mouse. *Expected:* the selection stays, the focus doesn't jump to the input, and Ctrl/⌘+C copies the words.

## I. A phone

35. Open the console on the phone (drawer, Console). *Expected:* the page fits the width, long lines wrap instead of running off the side, and nothing needs sideways scrolling.
36. Tap the input. *Expected:* the keyboard opens without zooming the page; it doesn't capitalise the first letter or correct `rg` into a word; its Enter key says Go (or shows an arrow) and runs the line. The input and the path line stay visible above the keyboard. On iOS Safari, note exactly what happens; this is the likeliest place for a difference, and a covered input is a finding.
37. On an iPhone or a phone with a home indicator, with the keyboard closed: *Expected:* the input sits above the home indicator, not under it, and can be tapped.
38. Steps 5, 15 and 27 on the phone. *Expected:* the same lines, `>` and `→` aligned, the picture within the width. Scroll the output with a finger; tapping in it brings the keyboard back.

## J. Leaving, reloading, another user

39. Type a few commands, then "Back to the notes", open a note, and come back to the console. *Expected:* the directory, the scrollback and the history (Up) are as you left them.
40. Reload the page. *Expected:* everything gone: only the welcome line, the path `/`, and Up brings nothing back.
41. Type a few commands as Alice, among them `cd /Gartenarbeit/Shopping` and `cat plan.md`. Sign out, and in the same tab sign in as Bob. Open the console. *Expected:* the welcome line and `/`, nothing of Alice's output, and Up brings nothing back.
42. In the browser's developer tools, under Application (Storage in Firefox), look at local storage and session storage for the instance. *Expected:* nothing that holds a command, a path, `Tomaten` or any output.

## K. A member of a shared vault

Bob needs an account, and the sharing happens in the web app; [test-playbook-collab.md](test-playbook-collab.md), parts A to C, has the long version. In short: as Alice, Admin, Invitations, make an invitation for a user; open the link in Bob's browser and create the account. To share: the vault's ⋯ menu in the drawer, **Share…**, invite Bob with a role (the password is asked), and open the link in Bob's browser to accept; Bob's role is changed later in the same dialog's access list.

43. As Alice: share `Gartenarbeit` with Bob as read only. As Bob: accept, naming it `Garten Alice`. In Bob's console: `ls /`. *Expected:* Bob's own vaults and `Garten Alice`, nothing else of Alice's.
44. `cd "/Garten Alice/Shopping"`, `cat plan.md`. *Expected:* `Tomaten`, under Bob's name for the vault. `cd /Gartenarbeit/Shopping`, if Bob has no vault of that name. *Expected:* `not a folder`: the owner's name means nothing to Bob.
45. `rm plan.md`. *Expected:* `rm: not in this console, which only reads`, the console's refusal and not the read-only member's. As Alice: the note is unchanged.
46. As Alice: raise Bob to write. As Bob: `rm plan.md` again. *Expected:* still refused: the console doesn't write for anyone.
47. `history plan.md` and `history "/Garten Alice"`. *Expected:* Alice by her name, Bob's own writes, if any, as his; other members as "another user".

## L. What the console can't show

48. Note for the record: a key's rights, the MCP layer and the write tools can't be tested here. As Alice, the console runs with an owner's full rights, so nothing in it says what a `read` key would get. Note anything on the page that suggests it can test a key's rights.
49. What was unclear, and what would you change first? Quote the sentence as you would write it.
