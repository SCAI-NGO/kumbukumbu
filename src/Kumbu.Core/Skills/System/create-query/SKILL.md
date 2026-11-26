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
   - Every view needs a `name`: one without makes the whole base an error, and Obsidian shows nothing. A sort key needs `direction: ASC` or `direction: DESC`, in capitals; without one, or with `asc`, it sorts nothing and the rows stay in path order.
   - A base runs over every file of the vault, as in Obsidian: attachments, `.base` files, the query itself and the archive too. Filter `file.ext == "md"` to keep Markdown files only, and `!file.inFolder("_archive")` to leave out the archive.
   - sh `help query` lists the functions, methods and fields; they are Obsidian's. A date has fields (`birthday.month`) and the methods `format("D MMMM YYYY")`, `time()` and `relative()`. A text is quoted ("People"). Text has no `upper()` and no `matches()`: a regular expression matches through its own method, `/^ann/i.matches(name)`. In `filter`, `map` and `reduce`, `value` is the item, `index` its place and `acc` (reduce) what came so far: `tags.filter(value.startsWith("#project"))`, `nums.reduce(acc + value, 0)`.
5. Run sh `query check <note>` and fix every error and every warning it names; the position counts in the expression it quotes. An error (syntax, or a limit: too-deep for nesting, too-large for more work or larger values than a run may take) stops the query; narrow the filters or simplify the expression. A warning (a function, method or formula that doesn't exist, a sort direction other than ASC or DESC, formulas that use each other in a circle) doesn't, as in Obsidian: the query runs with an error value in each cell it touches. A warning `regex` names a regular expression that is null everywhere: a flag other than g, i, m, s and u, a pattern that doesn't compile, or one beyond the part of RE2 that Kumbu² runs alike on the server and offline (Obsidian runs more): no lookaround, backreferences, named groups, `\p{…}` or `[\s\S]` (write `.` with the s flag), a lone `{`, `}` or `]` escaped, and at most 64 positions written out, a count multiplying its body (`\d{4}` is 4, `.{0,500}` too many). Write it within that, or shorter: a few alternatives, or a test with `contains()` instead.
6. Run sh `query execute <note> --param name=value` with a value the user cares about, and look at the rows: are the right notes there, in the right order? Before the table, a line such as `2 errors in formula.age: Invalid operator between String and Number` counts the cells of a column that hold an error, with the first message; the cells show `error: …`. A calculation between types that don't fit, a field or method a value hasn't, or `number()` of a text that isn't one gives such an error, and a filter that gives one drops the row. Fix what the line names: correct the expression, or filter out the notes it can't handle. Show the user the result and the note's link.

Without write on that vault (the write tool refuses), don't look for another way: give the user the note's path and content, and they add it in Kumbu².
