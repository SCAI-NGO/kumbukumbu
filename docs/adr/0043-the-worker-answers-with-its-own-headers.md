# 0043. Let the service worker answer a kept file with headers of its own

- **Date:** 2026-11-26
- **Status:** Accepted
- **Decided by:** Chris, with the fixes of 0.7.0
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.5; [fixes of 0.7.0](../superpowers/specs/2026-11-26-fixes-0-7-0-design.md)

## Context

This record supersedes [0021](0021-the-service-worker-checks-what-it-serves.md), which lets the app shell's service worker serve a kept file only while its bytes are the build's, hashed against the list the build writes into the worker's own script. 0021 checks a kept file's bytes; the rest of the answer came from the cache as it was stored. Building the whole answer in the worker makes an offline start run under the same headers as an online one.

## Decision

The worker never hands on the response it finds in the cache. From the bytes it has just checked it builds a new response, status 200, with headers it sets itself, the ones the server gives the same file (`src/Kumbu.Server/Api/Security.cs`): the content type from the build's list, `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, and the app's policy, or the password hash worker's for `/assets/kdfWorker-*.js`. A file fetched from the server is kept the same way: its bytes, in a response the worker made, not the network's. 0021's check of the bytes stays as it was.

## Why

- The bytes are the one part of a kept answer the worker checks; building the answer from them leaves nothing else to check.
- The headers aren't the worker's to choose: they are the server's for the same file, so an offline start runs under the same policy as an online one. The build writes each file's content type beside its hash; the two policies are a copy in `web/sw/policy.ts`, which a test holds equal to the server's, since the image's web build doesn't see the server's sources.
- A new response has no URL of its own, so a module's imports resolve against the URL the page asked for: the app's own.
- Checking the cached headers against an allowed list was weighed and lost: it would have to know every header a browser might act on, now and later; setting them ourselves needs no such list.

## Consequences

- The worker's answer for a kept file is the server's in all but the headers it leaves out (`Cache-Control`, `ETag`), which the worker's cache makes moot.
- A new kind of file in the build needs its content type in `web/sw/shell.ts`; the build stops until it has one.
- A change to either policy in `Security.cs` needs the same change in `web/sw/policy.ts`; the test fails until both agree.
