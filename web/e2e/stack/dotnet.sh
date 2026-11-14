#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# The fallback when the images can't be built (E2E_STACK=dotnet): the server from `dotnet publish` (Release) with
# the built web app, against a fresh MongoDB container, with the compose file's settings. No PDF sidecar.
# dotnet.sh up | down | bootstrap-admin
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
WORK="${TMPDIR:-/tmp}/kumbu-e2e-dotnet"
MONGO=kumbu-e2e-mongo
server_env() {
  export ASPNETCORE_URLS=http://127.0.0.1:8080 ASPNETCORE_WEBROOT="$WORK/wwwroot" ASPNETCORE_ENVIRONMENT=Production
  export Kumbu__Mongo__ConnectionString="mongodb://127.0.0.1:27019/?directConnection=true"
  export Kumbu__DevToken= Kumbu__PublicUrl=http://localhost:8080 Kumbu__Search__Enabled=false
  export Kumbu__Skills__Marketplaces__Enabled=false Kumbu__Seed__Path="$E2E_VAULTS" Kumbu__Pdf__Url=
  export Kumbu__Recovery__Path="$WORK/recovery" Kumbu__Deletions__Path="$WORK/deletions" Kumbu__Search__ModelPath="$WORK/models"
}

case "${1:-}" in
  up)
    "$0" down
    mkdir -p "$WORK/recovery" "$WORK/deletions" "$WORK/models" "$E2E_VAULTS"
    docker run -d --name "$MONGO" --ulimit nofile=20000:20000 -p 127.0.0.1:27019:27017 mongo:7.0 --replSet rs0 --bind_ip_all >/dev/null
    until docker exec "$MONGO" mongosh --quiet --eval 'try { rs.status() } catch (e) { rs.initiate({ _id: "rs0", members: [{ _id: 0, host: "127.0.0.1:27017" }] }) }; quit(db.hello().isWritablePrimary ? 0 : 1)' >/dev/null 2>&1; do sleep 1; done
    pnpm -C "$REPO_DIR/web" build >/dev/null
    cp -r "$REPO_DIR/web/dist" "$WORK/wwwroot"
    dotnet publish "$REPO_DIR/src/Kumbu.Server" -c Release -o "$WORK/app" -p:Version=0.0.0-dev >/dev/null
    server_env
    (cd "$WORK/app" && nohup dotnet Kumbu.Server.dll >"$WORK/server.log" 2>&1 & echo $! >"$WORK/server.pid")
    for _ in $(seq 1 120); do
      [ "$(curl -fsS http://localhost:8080/healthz 2>/dev/null)" = ok ] && exit 0
      sleep 1
    done
    echo "The server didn't answer /healthz within two minutes." >&2
    tail -50 "$WORK/server.log" >&2
    exit 1
    ;;
  down)
    if [ -f "$WORK/server.pid" ]; then kill "$(cat "$WORK/server.pid")" 2>/dev/null || true; fi
    docker rm -f -v "$MONGO" >/dev/null 2>&1 || true
    rm -rf "$WORK" "$E2E_VAULTS"
    ;;
  bootstrap-admin)
    server_env
    cd "$WORK/app" && exec dotnet Kumbu.Server.dll bootstrap-admin
    ;;
  *)
    echo "dotnet.sh up | down | bootstrap-admin" >&2
    exit 2
    ;;
esac
