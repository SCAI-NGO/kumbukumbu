# Public Deployment and the CTO Document Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kumbu² runs publicly at `kumbu.tidesson.net` behind Traefik with Let's Encrypt, with the dev token off; and a document explains its encryption at rest and security architecture to a CTO.

**Architecture:** An override file `compose.traefik.yaml` adds Traefik labels and the external Traefik network to the existing `compose.yaml`; nothing else in the deployment changes. The document is written from the code and the concept as built, after the accounts backend and the web app.

**Tech Stack:** Docker Compose, Traefik v3 labels, Markdown (and a PDF rendered from it with pandoc for the meeting).

**Spec:** `docs/superpowers/specs/2026-10-24-accounts-and-web-design.md` §2 (instances) and §5 (the CTO document).

## Global Constraints

- `compose.yaml` keeps working locally without Traefik.
- Variables: `KUMBU_HOST` (e.g. `kumbu.tidesson.net`), `KUMBU_TRAEFIK_NETWORK` (the external network Traefik watches), `KUMBU_TRAEFIK_CERTRESOLVER` (the resolver name in Chris's Traefik), `KUMBU_PUBLIC_URL` (`https://$KUMBU_HOST`), `KUMBU_TRUSTED_PROXY=true`, `KUMBU_SEARCH_MODEL=arctic-l`, working-set cap 2 GB; `KUMBU_DEV_TOKEN` unset.
- No host port in the Traefik override; MongoDB never on a network Traefik sees.
- The document in British English; every claim matches the code as built (cite files where it helps); limits stated plainly.

## Review Focus

1. With the override, `docker compose config` shows no published port for `kumbu`, `mongo` only on the internal network, and the dev token absent. Check in Task 1.
2. A request over plain HTTP is redirected to HTTPS by Traefik, and the app sets `Strict-Transport-Security`. Check in Task 2.
3. The document claims nothing the code doesn't do (e.g. "the operator can't read notes" is false while a request runs; the document must say what the operator can and can't do). Check in Task 3.

---

### Task 1: `compose.traefik.yaml`

**Files:** Create `compose.traefik.yaml`; modify `.env.example` (a block for the public instance), `README.md` (a "Public deployment behind Traefik" section: `docker compose -f compose.yaml -f compose.traefik.yaml up -d`, then `docker compose exec kumbu kumbu bootstrap-admin` and open the printed link).

- [ ] Steps: write the override (router `kumbu` with `Host(${KUMBU_HOST})`, entrypoint `websecure`, `tls.certresolver=${KUMBU_TRAEFIK_CERTRESOLVER}`, service port 8080, `traefik.docker.network=${KUMBU_TRAEFIK_NETWORK}`; `ports: !reset []`; the external network; the environment block above) → `docker compose -f compose.yaml -f compose.traefik.yaml config` matches Review Focus 1 → commit `Add a Compose override for a public instance behind Traefik`.

### Task 2: Deploy to `kumbu.tidesson.net`

Done by the controller with Chris (it touches his server): pull, `.env` from the example, up, bootstrap the admin, invite a second account, upload a real Obsidian folder, create a key, connect Claude Code, check Review Focus 2, a headless blind run against the public instance. Results go into the phase notes.

### Task 3: `docs/security-architecture.md` and its PDF

**Files:** Create `docs/security-architecture.md`; `docs/security-architecture.pdf` rendered with `pandoc` (not committed if it can be regenerated; attach to the meeting).

Content (spec §5): threat model with honest limits; keys and slots with a diagram (Mermaid source in the document); who can do what (admin, operator, database dump, stolen API key, lost laptop, AI provider); encryption at rest in detail (AES-256-GCM with context, sealed paths and names, keyed hashes, sealed embeddings and checkpoints, the user key, Argon2id parameters, sessions as slots); revocation and recovery (no password reset, by design); deployment hardening; what comes next (GitHub login, passkeys with PRF, OAuth for connectors).

- [ ] Steps: write from the code and the concept as built → a reviewer checks every claim against the code (Review Focus 3) → render the PDF → commit `Describe the security architecture for a CTO`.
