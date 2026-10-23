#! /bin/sh

git pull
docker compose up -d --build
dotnet test
echo "don't forget to restart claude code :-)"
