---
name: kumbu-binary-upload
description: Put a file you have or made (a generated image, a screenshot, a PDF) into the user's vault and link it from a note. Use when the user says "save this image in my notes", "put the PDF in the vault", "add the screenshot to the note", or when you made a picture or document that belongs in a note.
---
# Upload a file into the vault

Notes are text and go in with the write tool (an SVG may too); an image, a PDF or any other file can't. The `upload` tool hands out a one-time URL, and you send the file there from a shell.

1. Decide the path inside the vault, with the file's own extension: `/Work/images/diagram.png`, `/Work/data/sales.xlsx`. Kumbu² keeps any file, up to the server's bound per file (1 GiB unless its admin set another; `upload`'s answer names it). A name ending in .png, .jpg, .jpeg, .gif, .webp, .svg or .pdf must hold that format, which Kumbu² checks from the bytes; any other file is kept as it is, and offered as a download. A file larger than 15 MB is kept, but not read (`pdf`, `img` and their kin say so); an SVG can have at most 15 MB. The vault must exist and you need write access.
2. To replace a file that exists, `cat` it for its `version` token and pass it to `upload`. Without `version`, the `upload` tool itself refuses a path that exists: pick a free path or ask the user. A 409 from the PUT means the file changed meanwhile; `cat` it again and ask for a new URL.
3. Call `upload` with the path (and `version`). Run the curl line it answers, with `<local file>` the file's path in your shell: `curl -sS -X PUT --data-binary @<local file> '<url>'`. Use the URL exactly as given, don't rewrite its host. It must be HTTPS: over plain HTTP the PUT is refused and the URL used up.
4. The answer (201 new, 200 replaced) is JSON with `markdown` and `link`. Put `markdown` into the note with `edit` where the file belongs: `![name](/Folder/file.png)`, a PDF as `[name](/Folder/file.pdf)`, the path inside the vault without the vault's name. Show the user the `link`, which opens the file in Kumbu².
5. A URL works once, for 10 minutes. 429 (too many requests) doesn't use it: wait and retry the same URL. Any other failure does (a name that doesn't match its format, larger than the bound, conflict, 401 the URL is unknown, used or expired, 403 the key or connection no longer works or lacks the right, 404 a vault it no longer opens, 503 a vault is being re-encrypted): fix the cause and call `upload` again. A refusal of the URL or its credential (401, 403, 404, 503) ends "Ask for a new upload URL with the upload tool."
6. If the host can't be reached from your shell (connection refused, a DNS failure, a proxy's 403 "host not allowed"), the sandbox blocks it: tell the user to allow the Kumbu² host in their client's sandbox or network settings (the README's "Agents uploading files" says how), then call `upload` again for a fresh URL. Don't send the file as base64 through a tool argument; it doesn't fit and corrupts.

Without a shell that reaches the server, say so, and tell the user the file can be added in Kumbu² itself.
