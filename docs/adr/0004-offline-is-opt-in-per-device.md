# 0004. Make offline opt-in per device

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §3a "Turning it on"

## Context

An offline copy puts the person's notes, sealed, on a device's disk, and changes made offline live only there until they upload.

## Decision

Offline is off by default, per device. A device where the person ticked "Keep me signed in", or that runs Kumbu² as an installed app, is invited once ("Keep your notes on this device for when you're offline?"). Turning it on and off lives in Settings, Offline, which is reachable offline too, and the offline bar and the sidebar's mark lead there in one tap.

## Why

- Confidentiality first: a copy on a borrowed or shared computer must never come about unasked.
- Space and data: the person sees each vault's size before choosing it.
- The two signals that trigger the invitation are the ones where the device plainly is the person's own.

## Consequences

- People who never accept the invitation keep today's behaviour exactly: no copy, no service-worker data beyond the app shell.
- The setting must be easy to find, since turning it off again is a security action.
