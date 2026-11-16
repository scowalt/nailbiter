#!/bin/sh
set -eu

version=0.76.2
sha256=bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf
archive="${RUNNER_TEMP:?Runner temp is required}/trivy.tar.gz"
directory="$RUNNER_TEMP/trivy"
curl --fail --silent --show-error --location --proto '=https' --output "$archive" "https://github.com/aquasecurity/trivy/releases/download/v$version/trivy_${version}_Linux-64bit.tar.gz"
printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check
mkdir "$directory"
tar -xzf "$archive" -C "$directory" trivy
printf '%s\n' "$directory" >> "${GITHUB_PATH:?GitHub path export is required}"
