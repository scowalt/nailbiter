#!/bin/sh
set -eu

expected=${RUNNER_TEMP:?The runner temporary directory is required}/dotnet/dotnet
version=$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)".*$/\1/p' apps/backend/global.json)

found=$(command -v dotnet || true)
if [ "$found" != "$expected" ]; then
  echo "::error title=.NET SDK not on PATH::dotnet resolves to '$found', not the pinned SDK at $expected."
  exit 1
fi

actual=$(cd apps/backend && dotnet --version)
if [ "$actual" != "$version" ]; then
  echo "::error title=.NET SDK version mismatch::dotnet --version is '$actual', but apps/backend/global.json names $version."
  exit 1
fi

echo "command -v dotnet: $found"
echo "dotnet --version: $actual"
