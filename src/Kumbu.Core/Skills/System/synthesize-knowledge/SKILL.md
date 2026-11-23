---
name: synthesize-knowledge
description: Find what the user's notes know together but never say, notes or topics that share mechanisms, causes or ideas without linking each other, and write them up as hypotheses with their evidence, in the way of Don Swanson's literature-based discovery. Use when the user asks "what can we learn from everything we wrote about X", "how could X relate to Y", "what in my notes could help with X", "what am I missing", or wants hidden connections found.
---
# Synthesize knowledge

Swanson found in 1986 that two literatures which never cited each other held a discovery between them: fish oil lowers blood viscosity, Raynaud's patients have high blood viscosity, so fish oil might help them. A is the topic, C the distant notes, B the bridges: the mechanisms both speak of. Your job is Arrowsmith's and the judge's at once, so write the evidence down where the user can check it.

Two modes. **Bridge**: the user names two notes or topics, A and C ("how could X relate to Y?"). **Explore**: only A is given ("what can we learn from what we wrote about X?").

1. Pin A, and C in a bridge. Find their notes with `semsearch` or `rg` if the user named topics, and read them (`cat`, `outline`). Name A's silo: its folder, tag or vault.
2. Name the B terms: the mechanisms, properties, causes, methods and open questions A's notes speak of, in your own words. Five to fifteen, each with the section it stands in.
3. Find candidates, reading only the sections you pick:
   - Bridge: `similar --between <A> <C> --without-tag synthesis/hypothesis` for sections close to both, and `graph --path <A> <C> --without-tag synthesis/hypothesis` for bridges by links.
   - Explore: `similar <A> --unlinked --outside <A's silo> --without-tag synthesis/hypothesis`, also for A's most telling sections (`similar "<A>#Section" …`), and `semsearch --outside <A's silo> --without-tag synthesis/hypothesis <B term>` for the B terms the notes name in other words.
   `similar` prints each hit's link distance: `not connected` and `other vault` are what nobody connected yet, `2 steps` shares a neighbour, `1 step` is already linked. Its scores compare within one run only; read the sections, don't trust the numbers.
4. Check each candidate pair. Not a finding: a pair already linked (`graph --path`), a note that already names both (`rg -l` for A's term, then C's in those notes), or a candidate that only shares words, not a mechanism. Better: notes from different sources, people or times (front matter, `blame`, `history`).
5. Judge. Keep at most five hypotheses: those with the most independent B links and the clearest mechanism. For each, say why it could be wrong. If nothing holds up, say so; an empty result is a result.
6. Write one report note with the write tool, in the user's own vault beside A or where the user says (in a shared vault every member reads it): a title naming A and the date, front matter `source: synthesized` and `tags: [synthesis/hypothesis]`, and a review date two weeks out (`calendar set <report> --when <date> --task`). Per hypothesis: A, C, each B link as a link to the section it stands in, then your reading in a callout:
   ```markdown
   > [!inferred]
   > If both hold, …
   ```
   What the notes say stays plain and linked; what you make of it goes into `[!inferred]`. Never change A or C and never add links to them: propose the links in the report, and add them only when the user says so.
7. Report in the chat: each hypothesis in a sentence, the report note's path, and what you looked at and left out.

Without write on the vault, give the user the report's text instead. With semantic search off or the notes not indexed yet, `similar` says so: go on with `semsearch --words`, `rg` and `graph`, and tell the user the run was by words only.
