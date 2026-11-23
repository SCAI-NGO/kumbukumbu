#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Builds the stack from this checkout and starts it from nothing; returns once /healthz answers ok.
# E2E_STACK=dotnet runs the server from `dotnet publish` instead (stack/dotnet.sh); E2E_PROXY_CA=<file> builds
# behind a proxy that re-signs TLS; E2E_SEARCH=1 turns search by meaning on (compose.search.yaml).
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
if [ "${E2E_STACK:-compose}" = dotnet ]; then exec "$STACK_DIR/dotnet.sh" up; fi

"$STACK_DIR/down.sh"
own_or_absent "$E2E_VAULTS"
rm -rf "$E2E_VAULTS"
mkdir "$E2E_VAULTS"
if [ -n "${E2E_PROXY_CA:-}" ]; then
  ca="$(mktemp -d)"
  cp "$E2E_PROXY_CA" "$ca/proxy-ca.crt"
  printf '%s\n' 'ARG BASE' 'FROM ${BASE}' 'COPY proxy-ca.crt /usr/local/share/ca-certificates/proxy-ca.crt' \
    'ENV NODE_EXTRA_CA_CERTS=/usr/local/share/ca-certificates/proxy-ca.crt' \
    'RUN if command -v update-ca-certificates >/dev/null; then update-ca-certificates >/dev/null; fi' > "$ca/Dockerfile"
  while read -r name base; do
    docker build -q --build-arg BASE="$base" -t "kumbu-e2e-ca-$name:local" "$ca" >/dev/null
  done < <(grep -o '"[^"]*": docker-image://kumbu-e2e-ca-[a-z0-9]*' "$STACK_DIR/compose.proxy-ca.yaml" \
    | sed -E 's/^"([^"]*)": docker-image:\/\/kumbu-e2e-ca-([a-z0-9]*)$/\2 \1/')
  rm -rf "$ca"
fi
# The model's volume outlives the stack (compose.search.yaml): created once, kept between runs.
if [ "${E2E_SEARCH:-}" = 1 ]; then docker volume create kumbu-e2e-models >/dev/null; fi
compose up -d --build --wait
healthy=
for _ in $(seq 1 60); do
  if [ "$(curl -fsS http://localhost:8080/healthz 2>/dev/null)" = ok ]; then healthy=1; break; fi
  sleep 1
done
# With search on, once the model is loaded (downloaded first on a fresh volume, about 630 MB): every run's first test
# then starts from the same point, whatever the download took.
if [ -n "$healthy" ] && [ "${E2E_SEARCH:-}" = 1 ] && [ "${E2E_EMBEDDINGS_DEFAULT:-internal}" = internal ]; then
  for _ in $(seq 1 900); do
    compose logs --no-color kumbu 2>/dev/null | grep -q "Embedding model loaded" && exit 0
    sleep 1
  done
  echo "The embedding model wasn't loaded within 15 minutes." >&2
  compose logs --tail 50 kumbu >&2
  exit 1
fi
[ -n "$healthy" ] && exit 0
echo "The server didn't answer /healthz within a minute." >&2
compose logs --tail 50 kumbu >&2
exit 1
