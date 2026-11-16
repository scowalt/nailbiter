#!/bin/sh
set -eu

version=1.4.4
sha256=bf8ccbfb2b0b80159e0f9a6d15c55317add8077534dbc6130a3edb52fccb395c
archive="${RUNNER_TEMP:?Runner temp is required}/bun.zip"
directory="$RUNNER_TEMP/bun"
curl --fail --silent --show-error --location --proto '=https' --output "$archive" "https://github.com/oven-sh/bun/releases/download/bun-v$version/bun-linux-x64.zip"
printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check
mkdir "$directory"
unzip -q -j "$archive" bun-linux-x64/bun -d "$directory"
ln -s bun "$directory/bunx"
installed=$("$directory/bun" --version)
if [ "$installed" != "$version" ]; then
  printf 'bun --version printed %s, expected %s\n' "$installed" "$version" >&2
  exit 1
fi
printf '%s\n' "$directory" >> "${GITHUB_PATH:?GitHub path export is required}"
