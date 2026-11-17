---
name: kumbu-binary-upload
description: Put a file you have or made (a generated image, a screenshot, a PDF) into the user's vault and link it from a note. Use when the user says "save this image in my notes", "put the PDF in the vault", "add the screenshot to the note", or when you made a picture or document that belongs in a note.
---
# Upload a file into the vault

Notes are text and go in with the write tool; an image or PDF can't. The `upload` tool hands out a one-time URL, and you send the file there from a shell.

1. Decide the path inside the vault, with the extension of the format: `/Work/images/diagram.png`. Kumbu² keeps PNG, JPEG (.jpg, .jpeg), GIF, WebP, SVG and PDF, at most 15 MB each, and checks the format from the bytes, not the name. Convert anything else first, or tell the user it can't be kept. The vault must exist and you need write access.
2. To replace a file that exists, `cat` it for its `version` token and pass it to `upload`. Without `version`, a path that exists is refused (409, with the file as it is now): pick a free path or ask the user.
3. Call `upload` with the path (and `version`). Run the curl line it answers, with `<local file>` the file's path in your shell: `curl -sS -X PUT --data-binary @<local file> '<url>'`. Use the URL exactly as given, don't rewrite its host. It must be HTTPS: over plain HTTP the PUT is refused and the URL used up.
4. The answer (201 new, 200 replaced) is JSON with `markdown` and `link`. Put `markdown` into the note with `edit` where the file belongs: `![name](/Folder/file.png)`, a PDF as `[name](/Folder/file.pdf)`, the path inside the vault without the vault's name. Show the user the `link`, which opens the file in Kumbu².
5. A URL works once, for 10 minutes. Any failed PUT has used it (wrong format, too large, conflict, refused): fix the cause and call `upload` again. 401 means the URL is unknown, expired or used; 403 that the key or connection no longer works or lacks the right to write there. Don't retry the same URL.
6. If the host can't be reached from your shell (connection refused, a DNS failure, a proxy's 403 "host not allowed"), the sandbox blocks it: tell the user to allow the Kumbu² host in their client's sandbox or network settings (the README's "Agents uploading files" says how), then call `upload` again for a fresh URL. Don't send the file as base64 through a tool argument; it doesn't fit and corrupts.

Without a shell that reaches the server, say so, and tell the user the file can be added in Kumbu² itself.
