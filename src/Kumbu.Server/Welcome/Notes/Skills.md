# Skills

A skill is a set of instructions for a task you do again and again: how you like a release post written, how you sort receipts, what your weekly review asks. Your AI looks for a skill that fits the task in front of it and follows it, so you explain the task once.

## The vault Skills

Your account has a vault called Skills for your own skills. Each skill is a folder there with a note `SKILL.md` in it: the skill's name, a description that says when it applies, and the instructions. Beside it go any examples, references or scripts the skill needs. Skills is a vault like any other, with history, trash and search, except that it can't be deleted, renamed or shared with other people.

A skill that belongs to the work of one vault goes into a folder `_skills` at the top of that vault instead, `_skills/release-notes/`, say. Everyone who has that vault sees it, and every list of skills says who last changed it.

Your AI sees the skills in Skills only if its key or connection has the vault Skills; without it, it still sees the built-in skills and those in the `_skills` folders of the vaults it has. A new key or connection starts with Skills set to read; for keys and connections made before, add it in drawer → Access. To let your AI write skills, give it write on Skills.

## Making a skill

Kumbu² comes with a skill for making skills, `create-skill`. Say to your AI:

- "Make me a skill that writes release notes the way I like them."
- "Make me a skill for checking my monthly expenses, and ask me what to look out for."

It asks what the skill is for and when it should be used, writes the folder, and checks it. Without write on Skills it gives you the files to add yourself.

## Making notes of a PDF

A second built-in skill, `ingest-attachments`, turns a PDF into notes: drop a pump manual into a folder Inbox and say to your AI "Make notes of the pump manual in Inbox." It asks whether you want one note or a folder of them, writes the notes with the manual's pictures and diagrams where they stood, and asks whether to keep, archive or delete the PDF; archived, it moves to the vault's `_archive/` folder, out of the way of search, and the notes' links follow it there.

## The Skills screen

In the drawer, **Skills** lists every skill you and your AI can see, where each one comes from and who last changed it. From there you copy or move a skill to another vault, download it as a .zip, and share it with a link of its own, which looks like {{server}}/k#… and is shown once. **Try** shows which skills your AI would find for a request. Your server may also offer **Marketplaces**: collections of skills on GitHub that you add skills from.

## Other people's skills

A skill tells your AI what to do, so a skill someone else wrote can tell it things you don't want. Read a skill before you add it from a link or a marketplace, and look at who changed a skill in a shared vault. A copy you added is never updated by itself: Kumbu² tells you when there is a newer version, and you review the changes first. Kumbu² never runs a skill's scripts.

Back to [[Start here]].
