# ADR-009: Authentication

## Context

Every service did its own login. Users asked for single sign-on.

## Decision

OIDC with Keycloak. Access tokens live 15 minutes, refresh tokens 8 hours; services validate tokens locally against the JWKS.

## Consequences

Keycloak becomes critical infrastructure and runs in two zones.
