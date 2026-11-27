# 0021. Let the service worker serve a kept file only while its bytes are the build's

- **Date:** 2026-11-23
- **Status:** Superseded by [0043](0043-the-worker-answers-with-its-own-headers.md)
- **Decided by:** proposed with the fix, from the review of the offline mode; Chris confirms in review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.5

## Context

The app shell's service worker keeps one build's files and answers them from its cache first, so the app opens without a network. CacheStorage belongs to the origin, not to the worker: any page of the origin can write the worker's cache. One run of script in the origin, from a compromised dependency in one build or from someone with a minute at the developer tools of a browser where Kumbu² was used, could replace a kept script, and the worker would serve it on every later start, online too, across sign-outs and password changes, and even into the next build's cache, which the old page can write before its reload. That script would see the password typed into the sign-in form.

## Decision

The build writes each kept file's SHA-256 beside its name into the worker's script, hashed from the files as written. Before serving a kept file the worker checks its bytes against that hash; one that differs is deleted and fetched from the server, which is kept again only if it matches. Without a network a changed file isn't served at all: the start fails rather than run it.

## Why

- The worker's own script is out of any page's reach: the browser keeps it and compares it with the server's at every navigation (`updateViaCache: "none"`), so the list is a trustworthy reference.
- Subresource Integrity on `index.html`'s tags was weighed and lost: it covers the entry files only, not the chunks loaded later, and not the offline start, where the cached page itself would be the attacker's.
- Hashing each file on every start costs a few milliseconds for the few megabytes of the app, against a cache that would otherwise be a place for script to stay for good.

## Consequences

- An offline start needs every file it loads intact; a damaged entry is fetched again at the next online start.
- Anything between the server and the browser that rewrites the app's files (a proxy that minifies or injects into HTML) makes them fail the check: online they are fetched each time instead of kept, and offline the app doesn't start. Kumbu² serves its files unchanged; such a proxy has to leave them alone.
