# Keep me signed in — Design

**Date:** 2026-10-30 · **Status:** agreed in chat, written for review · **Release:** v2.5 (0.2.5)

## Intent

A web session ends after 30 minutes idle and 12 hours at most (`src/Kumbu.Server/Accounts/Sessions.cs:18–19`). That is right for a shared or borrowed browser, and wrong for the scenario people ask for: Kumbu² as the notes app on one's own phone or laptop, used instead of Obsidian on the way. Signing in again every half hour is what makes people give up on that.

So the sign-in gets **"Keep me signed in on this device"**. It trades security for comfort, says so honestly, and leaves the choice to the user, as Kumbu² does elsewhere (prosumer defaults). A good password manager makes the rest painless.

The risk is real and needs naming: the session cookie's secret unwraps the user key (`Sessions.cs:10–15`), so whoever holds the cookie of a live session can read and write every vault of the user. A remembered session stretches that window from 12 hours to 30 days. What it must **not** stretch is the ability to make new ways in: a stolen cookie must not become an API key that outlives every sign-out.

## The rule

> A remembered session may do everything a normal session may, as long as a normal session would still be alive. After that, reading and writing go on, and whatever creates a new way in or takes data out in bulk asks for the password, which opens the normal window again.

It works like `sudo`, and needs nothing stored but times: no password, no extra wrapping of the user key.

## Goals

- A checkbox **Keep me signed in on this device** on the sign-in page, unticked by default.
- When ticked, a short warning in red below it: comfortable, but less safe; anyone who gets this device or browser unlocked gets into the notes until sign-out or 30 days are up. Use it only on a device of one's own, with a screen lock. (Wording to settle in the web app; British English.)
- A remembered session lasts at most **30 days** and ends after **7 days** idle.
- Inside it runs a **normal window** with today's limits: 30 minutes idle, 12 hours at most, counted from the last time the password was given (the sign-in, or a confirmation later).
- While the normal window is open, a remembered session behaves exactly like a normal one.
- Once it is closed, the guarded actions (below) answer that the password is needed; the web app asks for it in a small dialog, confirms it with the server, and repeats the action. The window is open again from then on.
- A normal session (box unticked) behaves exactly as today.

## Non-goals

- A list of signed-in devices with "last active": a good companion to this, but its own small feature, later. *(Claude's call, to keep this round small; "sign out other sessions" already exists, `Api/AccountApi.cs:141`.)*
- Limits chosen by the admin or the user: the four times are constants, as today's two are.
- Remembering anything else (the password, a PIN, a device key) or wrapping the user key a second way: the times are the whole mechanism.
- "Keep me signed in" for the join (invitation) and recovery flows: they sign in a normal session; the next sign-in can tick the box.

## Anti-goals (it failed if…)

- …a cookie stolen from a remembered session, used after the normal window closed, can create an API key, a connection, an Obsidian pairing, or an export, or widen a connection's rights.
- …a remembered session survives a password change, a recovery, a deactivation or "sign out other sessions": it must end exactly as a normal one does.
- …the web app's background polling keeps either clock alive (it does not touch a session today, `Sessions.cs:85–87`; that must hold for both clocks).
- …a normal session changes behaviour in any way.
- …the password dialog appears for reading or writing notes.

## Guarded actions

The rule, for the builder to apply to every endpoint: **an action that creates a credential, widens what one can reach, or takes vault data out in bulk.** At least:

- creating an API key (`POST /api/keys`, `Api/AccountApi.cs:146`), devices included;
- creating a connection (`POST /api/connections`, `Api/ConnectionApi.cs:63`) and the OAuth consent that issues a code for one (`OAuth/OAuthEndpoints.cs`, `OAuth/OAuthTokens.cs`);
- changing a connection's vaults or rights (`PUT /api/connections/{id}/vaults`, `Api/ConnectionApi.cs:64`);
- a pairing code for Obsidian (`POST /api/vaults/{vault}/pairing`, `Api/PairingApi.cs:30`);
- every export and bulk download: the export ticket and exports, a checkpoint's export, the files ZIP (`Api/ExportApi.cs:41–59`);
- admin: invitations and role changes (`Api/AccountApi.cs:155` and the user update).

Actions that already ask for the password each time (changing it, a new recovery key, re-encrypting, deleting the account or a vault, `Api/AccountApi.cs:139–143, 151–153`) keep doing so; their password counts as a confirmation too and opens the window. The existing preference "ask for the password before downloads" (`UserCatalog.AsksPasswordForDownloadsAsync`) stays as it is and applies on top.

Exports and checkpoint exports already need the password on every call, through the export ticket (`Api/ExportApi.cs:61–70`), so they are guarded already; any password check of that kind opens the window too. The files ZIP is the one bulk download without it when the preference is off: with the window closed, the server requires the ticket for it as if the preference were on. The web app learns whether the window is open from `GET /api/session` (a new field), since the ZIP is a form post into a hidden frame (`web/src/attachments/downloadFiles.ts:14–42`) whose 401 it can't catch. *(Claude's call.)*

Switching a vault's embedding provider (`PUT /api/vaults/{vault}/embeddings`) is **not** guarded: the providers are the admin's, and a thief gains nothing by it that reading doesn't give. `security-architecture.md` names it as a deliberate exception. *(Claude's call.)*

Revoking, deleting and reading are never guarded: a thief can't gain anything there that the session doesn't already give, and the owner must always be able to clean up.

## Server

- The session slot (`UserSlotDocument`, kind `session`) records whether it is remembered, plus the normal window: when the password was last given and when the window was last used. A normal session's slot is as today (its own limits *are* the normal window).
- `ResolveAsync` applies the outer limits (30 days, 7 days idle) to a remembered slot, and tells the caller whether the normal window is open. `RequestCaller` carries that, so a guard is one check.
- A guarded action with the window closed answers **401** with a distinct problem type (for example `password-required`), not 403: the web app tells it from "signed out" and "not allowed" by that type.
- `POST /api/session/confirm` with the password: checked against the current password slot under the existing rate limit (`AccountLimits.PasswordCheck`), and only if the user key it opens is the one in force (`users.keyId`, as every write under the user key, `docs/security-architecture.md` §3). On success it opens the window; it starts no new session and changes no cookie.
- The cookie's `MaxAge` follows the session: 12 hours or 30 days (`Api/AccountApi.cs:460–465`).
- Touching (`touch: true`) moves both idle clocks, with the existing one-minute granularity.
- Password change, recovery, deactivation, "sign out other sessions" and account deletion end remembered sessions with the rest; they already end every session slot of the user (`Sessions.EndAllAsync`) and refuse slots older than the last credential change (`Sessions.cs:103–110`). A user-key rotation that keeps the current session (`Sessions.PrepareKeepAsync`) keeps it remembered or not as it was, with its times.
- Logs: a remembered sign-in says so; a confirmation is logged like a sign-in.
- **"Since your last sign-in"** (decided by Chris, 2026-10-30): a remembered session signs in once in 30 days, so the agent banner (changes by agents since the previous sign-in, `web/src/note/AgentBanner.tsx:34, 63`) would grow for weeks and the admin's user list ("last sign-in") would show a daily user as long gone. So a remembered session counts **visits**: a new visit begins with the first request after 30 minutes without one, the moment the normal window would lapse. The banner counts since the previous visit, and the admin list shows "last active" (the latest visit or sign-in) instead of "last sign-in". A confirmation of the password is no sign-in and moves neither.
- An interrupted re-keying resumes today only at a sign-in or recovery (`Rekeying.ResumePending`, `Api/AccountApi.cs:180, 234`), which a remembered session may not see for 30 days. So the first request of a session after the server started (once per user and process) resumes it too, as a sign-in would. *(Claude's call.)*

## Web app

- The checkbox and the warning on the sign-in page.
- One password dialog for every guarded action: on the `password-required` answer, ask, confirm, repeat the request once. Cancelling cancels the action. A wrong password says so in the dialog; a rate-limit answer shows its wait.
- The files ZIP takes the password path (ticket) while the window is closed, as above.
- The agent banner and the admin list follow visits, as above.
- No other change: a remembered user sees the app as today.

## Security documentation

`docs/security-architecture.md` gets the remembered session in the sessions section and a row (or an extension of the existing one) in the threat table: **stolen cookie of a remembered session** — can: read and write the user's vaults for up to 30 days (7 idle), until sign-out, a password change or "sign out other sessions"; cannot, once 30 minutes idle or 12 hours have passed since the password was last given: create keys, connections or pairings, widen rights, export. With the reasoning above (no new kind of access, a longer window, no persistence beyond it).

## Tests

- Server (xUnit, with a fake `TimeProvider`): both clocks for remembered and normal sessions; the guard on each guarded endpoint with the window open and closed; confirmation (right, wrong, rate-limited, after a rotation); the background poll moving neither clock; every way of ending sessions ending remembered ones; `PrepareKeepAsync` keeping the flag.
- Web (Vitest): the checkbox and warning; the password dialog repeating the action once, cancel, wrong password.
