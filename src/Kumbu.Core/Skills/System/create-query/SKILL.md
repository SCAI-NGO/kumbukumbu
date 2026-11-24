---
name: create-query
description: Write a named query for the user, a note that lists notes by their properties (Obsidian Bases with parameters), and run it. Use when the user wants a list, table or overview of notes by what their frontmatter says ("all birthdays in November", "open tasks of a project"), or a query kept for next time.
---
# Create a query

1. Find out what the notes say: sh `query properties <folder>` lists each property with its type and how many notes have it. Read two or three of the notes with cat. A query is only as good as its properties: if the notes lack one, say so and ask before adding it.
2. Look for a query that already does it: sh `query ls`. Change or extend that one rather than writing a second.
3. Choose what the caller gives: a value that changes from call to call is a parameter (a month, a status, a project); the rest stays in the query. Each parameter has a type (text, multitext, number, checkbox, date, datetime) and is either required or has a default; `options` holds it to a few values.
4. Write the query note with the write tool, in a Queries folder of the vault unless the user keeps queries elsewhere:

   ````markdown
   ---
   type: query
   params:
     month:
       type: number
       required: true
       description: The month, 1 to 12.
   ---
   # Birthdays

   The people in People/ whose birthday is in a month, earliest day first.

   ```base
   filters:
     and:
       - file.inFolder("People")
       - birthday.month == this.month
   views:
     - type: table
       name: By day
       order: [file.basename, birthday]
       sort:
         - property: birthday
           direction: ASC
   ```
   ````

   - The prose says what the query gives and when to use it: `query inspect` shows it to whoever calls the query, so write it for them.
   - The base block is Obsidian's Bases and nothing else, so the note renders in Obsidian too: a parameter is read as `this.<name>`, never with another syntax.
   - sh `help query` lists the functions, methods and fields. A date has fields (`birthday.month`), not methods. A text is quoted ("People").
5. Run sh `query check <note>` and fix every error it names; the position counts in the expression it quotes.
6. Run sh `query execute <note> --param name=value` with a value the user cares about, and look at the rows: are the right notes there, in the right order? Show the user the result and the note's link.

Without write on that vault (the write tool refuses), don't look for another way: give the user the note's path and content, and they add it in Kumbu².
