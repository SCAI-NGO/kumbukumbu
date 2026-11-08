---
name: create-skill
description: Write a new skill for the user, a folder with SKILL.md that an AI follows for one kind of task. Use when the user asks for a skill, or wants a way of working kept for next time.
---
# Create a skill

1. Ask what the skill is for and when it should be used, unless the user said so: the task, the situations, what a good result looks like.
2. Choose a name: lowercase letters, digits and hyphens, at most 64 characters, e.g. write-as-alice. The skill's folder has the same name.
3. Write the description, at most 1024 characters: what the skill does and the situation it is for, in the words the user would use when asking. An AI picks a skill by its description alone.
4. Choose where it goes: /Skills/<name>/ for a skill the user wants everywhere, /<vault>/_skills/<name>/ for one that belongs to one vault's work (everyone with that vault sees it). Ask the user when it isn't clear.
5. Write <folder>/SKILL.md with the write tool: frontmatter with name and description, then the instructions, short and in order. Put long examples or references in files beside it (examples/post.md, references/style.md), and say in SKILL.md when to read them.
6. Run sh `skills check <folder>` and fix what it names.

Without write on that vault (the write tool refuses), don't look for another way: give the user each file's path and content, and they add them in Kumbu².
