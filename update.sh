#! /bin/sh

set -e

cd "$(dirname "$0")"

git pull --ff-only
dotnet test

start=$(date -u +%Y-%m-%dT%H:%M:%SZ)
docker compose up -d --build

# wait for the server to finish starting (the seeder runs before this line is logged)
for i in $(seq 30); do
	docker compose logs --no-log-prefix --since "$start" kumbu | grep -q 'Application started' && break
	sleep 1
done

if docker compose logs --no-log-prefix --since "$start" kumbu | grep -qE '^(fail|crit):'; then
	echo "kumbu logged errors on start:"
	docker compose logs --no-log-prefix --since "$start" kumbu | grep -A1 -E '^(fail|crit):'
	exit 1
fi

keys=$(docker compose exec kumbu ls /recovery)
if [ -n "$keys" ]; then
	echo "recovery keys waiting in /recovery - fetch and delete them:"
	echo "$keys"
fi
echo "and don't forget to restart claude code :-)"
