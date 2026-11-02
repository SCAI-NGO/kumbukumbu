# Test playbook: images and PDFs, across clients

A test of what a model really gets from `img` and `pdf`, against a real instance and real clients. It continues the first round in [attachments-client-research](attachments-client-research.md) §6.1, whose probes were large digits on white: they showed that an image arrives, not what a model can do with it. Sharing is tested separately.

Clients: Claude Code, claude.ai, ChatGPT, Grok, Muse. Le Chat is left out: it fails on any image part in a tool result (§6.1).

## Before you start (for the person running it)

### The answers stay out of the model's reach

A model that can find an answer proves nothing, so:

- **Nothing with answers goes into a vault:** not the key, not your list of what is on which page, not a client's results. `semsearch` would find them.
- **The PDF's table of contents stays neutral:** "Chapter 4" or "Holiday photos", never "Pie chart of revenue".
- **Questions need the picture:** ask for what no text says, such as the tallest bar or the colour of a car.
- **Each client gets a new chat,** with memory off where the client has it. A Claude Code session that made or looked at the probes doesn't count as a client.

### The probes

```sh
uv run tools/attachment-probes/make_probes.py <out>
```

Every run draws new answers. Upload `<out>/upload` to a vault **`Attachment Test`** that holds nothing else, into a folder `probes`; keep `<out>/key/answers.md` to yourself. The connector needs read access to this vault only, so the model can't search anything else or write down its results.

Add three pictures of your own to `probes`, with your own questions for section B:

- `photo.jpg`: a real photo with details, straight from a phone.
- `whiteboard.jpg`: a whiteboard or a page of handwriting, photographed at a slight angle.
- `screenshot.png`: a busy screenshot of a real application.

### The PDFs

Into a folder `pdfs` of the same vault:

| File | What is in it |
|---|---|
| `mix.pdf` | the pages of the table below, under 15 MB |
| `holiday-small.pdf` | about 50 pages of photos, under 15 MB |
| `holiday-large.pdf` | the same with more resolution, over 15 MB: its upload is meant to be refused |
| `locked.pdf` | a PDF with a password |

Before uploading, write down for `mix.pdf` the type you expect per page, and a question per page. Export from LibreOffice for `mix.pdf`, since printing to PDF may turn vector charts into pictures and so change the types; `pdf info` shows what the file really holds.

| Page in `mix.pdf` | Expected type | Why it is there |
|---|---|---|
| Contents, neutral | `text` | the start of a real document |
| Plain text with umlauts and ligatures | `text` | `rg` and `semsearch` must find these words |
| Two columns | `text` | is the extracted text still in reading order? |
| A table with lines | `text` | thin lines don't count as figures |
| A wide table from Calc, landscape | `text` | a turned page; do the columns stay apart? |
| One large Calc chart | `mixed` | vector drawing over 25 % |
| Two small charts side by side (about 11 % each) | `text`, with the nudge | the known miss of the threshold |
| A small logo in text | `text`, "small" | no nudge is expected |
| Text with a photo at half a page | `mixed` | |
| A phone scan, no text layer | `image` | seen, but not found by search |
| A scan with an OCR layer | `image` | seen, and its text found by `rg` |
| A photo with page margins | `image` or `mixed` | one image must cover 85 %; margins may take it below |
| Four photos on one page | `mixed` | no single image covers 85 % |
| A page turned by 90° | any | are crops still in the right place? |
| An empty page | `text` | |

### Checks in the web app, once

Tick these yourself; no model is needed.

- [ ] `holiday-large.pdf` is refused with a sentence that says why.
- [ ] `locked.pdf`: what happens on upload, in the lightbox and with `pdf info`? Nothing may hang or end in a bare error.
- [ ] The analysis of `holiday-small.pdf` finishes, and `cat` reports its pages.
- [ ] Gallery and lightbox show all files, `rotated.jpg` upright, `sideways.png` on its side as stored, `transparent.webp` with both shapes.
- [ ] The dashboard's Attachments row fills during the test: page types, `pdf` calls by mode, and `kumbu_pdf_text_pages_rendered_total` for looks at text pages.

### Running it

Use a new chat with the connector on, once per client. Send steps 11 and 21 alone first, phrased as in those steps and without the test's framing: the exact questions give away what the files hold, and a first look with no hints shows whether a model looks at pages by itself. Then paste everything below the line, with the placeholders in sections B and C replaced by your own questions. Compare the report with your key, and fill the table at the end of this file. In Claude Code, start in an empty folder outside the repository, with the key moved away: it can read local files, and the project's notes hold answers.

---

You are testing how well you can work with images and PDFs in Kumbu², a Markdown second brain you reach through the Kumbu² connector. Work only in the vault `Attachment Test`, and read only. Use only what the tool descriptions, `sh help` and the tools' answers tell you, as a user's assistant would.

**Be strict about what you really saw.** For every step, say whether an image actually reached you, or whether you only got text, a file reference or a sandbox you read it through (and with what). If you can't see something, say so: a wrong guess spoils the test, while "I got no image" is a useful result. Don't search the vault for answers.

Report each step as: what you called, what came back, your answer, and how sure you are. Note every moment you retried or guessed, even if the step worked in the end. That is the most valuable part of the result. Give the report here in the chat; write nothing into the vault.

### A. Images made for the test (`/Attachment Test/probes`)

1. `shapes.png`: What colour is the triangle? Which corner holds the star? How many circles are there?
2. `chart.png`: Which bar is the tallest, and about what value does it reach? What colour is bar C?
3. `small-text.png`: Exactly one row has the status FAILED. Which one, and what number is in it? Say which calls you needed.
4. `wide.png`: What colour is the circle, and what code stands below it? Say which calls you needed.
5. `rotated.jpg`: What size does `cat` report? Where does the arrow point, and which corner holds the blue square? Then crop the blue square alone, working out the coordinates from what you saw. Did the crop hit it, and how many tries did it take?
6. `animated.gif`: What colour is its second frame? Say what you did to find out.
7. `transparent.webp`: What do you see, in which colours?
26. `sideways.png`: Read the code in the small print, then crop it, and say how many tries it took.

### B. Real pictures (`/Attachment Test/probes`)

8. `photo.jpg`: Describe it, then: <your question>
9. `whiteboard.jpg`: Transcribe what you can read, and mark what you can't. Then: <your question>
10. `screenshot.png`: <your question about a small detail>

### C. A mixed document (`/Attachment Test/pdfs/mix.pdf`)

11. Pretend the user said: "What is in this PDF? Summarise it." Do what you would do for a real user, and list the calls you made. Afterwards say: did you look at any page as an image, which ones, and why? Which pages do you think you can't judge from text alone?
12. `pdf info`: give the page types. Do they match what the pages seem to be?
13. <your question about the large chart: read a value off it>
14. <your question about the two small charts side by side>. Did the tools point you to them?
15. <your question about the wide table: a value in a far column, in a late row>
16. <your question about the photo in the text>
17. <your question about the phone scan: what does it say?> Then search for one of its words with `rg` and `semsearch`, and report what they find and whether that fits what `help pdf` says.
18. Search for a word from the page with the OCR layer, and one with an umlaut from the text page, with `rg` and with `semsearch`.
19. On the page turned by 90°: `pdf info`, then crop one figure with the regions it gives. Did the crop hit it?
20. Show all pages as images with `--images=all`, as far as the tools allow. How many came with one call, and how did you go on? Did all of them reach you?
27. Find <a word of your choice from the text page> with `rg --regions`, then crop it. Did the crop hit it?

### D. A document of photos (`/Attachment Test/pdfs/holiday-small.pdf`)

21. Pretend the user said: "What did they see on this holiday?" Do what you would do for a real user. How many pages did you look at, how did you choose them, and did you tell the user what you left out?
22. <your question about one particular photo, by something on it rather than by its page number>
23. Try `pdf` on `/Attachment Test/pdfs/locked.pdf`, if the file is there. What does it say?

### E. Your view

24. Which answer or hint from the tools misled you or was missing? Which step cost the most images, and was there a cheaper way?
25. If you could change the tools for working with images and PDFs, what would you change first? Name up to three changes, each with the situation in this test that calls for it. Where it is a matter of wording, quote the sentence as you would write it. Say too if something is good as it is and should stay.

---

## Results (for the person running it, after the runs)

One row per client, each cell PASS, FAIL or the behaviour seen. Add the result to [attachments-client-research](attachments-client-research.md) as §6.2.

| Step | Claude Code | claude.ai | ChatGPT | Grok | Muse |
|---|---|---|---|---|---|
| Sees, rather than reads with OCR (1) | | | | | |
| Values off a chart (2, 13, 14) | | | | | |
| Finds small details, with a crop (3, 4, 10) | | | | | |
| Camera's rotation and crops (5, 19) | | | | | |
| Turns a sideways sheet, tries to a crop (26) | | | | | |
| Formats: GIF frames, WebP (6, 7) | | | | | |
| Real pictures (8, 9) | | | | | |
| Looks at pages by itself (11, 21) | | | | | |
| Tables and scans (15, 17) | | | | | |
| Several images in one result (20) | | | | | |
| A crop from `rg --regions` hits the word (27) | | | | | |
| Tells the user what it left out (21) | | | | | |

Write down what follows for the product: thresholds (`mixed`, 85 %), the 15 MB limit, wording of the hints. The round of 2026-11-01 is in [attachments-client-research](attachments-client-research.md) §6.2.
