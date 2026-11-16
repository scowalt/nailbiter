#!/bin/sh
set -eu

version=1.4.2
sha256=36368faef7527875d5ffa52e53cd48021741f2a83eb6208a8dd64068d422a913
archive="${RUNNER_TEMP:?Runner temp is required}/bun.zip"
directory="$RUNNER_TEMP/bun"
curl --fail --silent --show-error --location --proto '=https' --output "$archive" "https://github.com/oven-sh/bun/releases/download/bun-v$version/bun-linux-x64.zip"
printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check
mkdir "$directory"
unzip -q -j "$archive" bun-linux-x64/bun -d "$directory"
ln -s bun "$directory/bunx"
printf '%s\n' "$directory" >> "${GITHUB_PATH:?GitHub path export is required}"
