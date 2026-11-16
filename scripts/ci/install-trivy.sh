#!/bin/sh
set -eu

version=0.70.0
sha256=8b4376d5d6befe5c24d503f10ff136d9e0c49f9127a4279fd110b727929a5aa9
archive="${RUNNER_TEMP:?Runner temp is required}/trivy.tar.gz"
directory="$RUNNER_TEMP/trivy"
curl --fail --silent --show-error --location --proto '=https' --output "$archive" "https://github.com/aquasecurity/trivy/releases/download/v$version/trivy_${version}_Linux-64bit.tar.gz"
printf '%s  %s\n' "$sha256" "$archive" | sha256sum --check
mkdir "$directory"
tar -xzf "$archive" -C "$directory" trivy
printf '%s\n' "$directory" >> "${GITHUB_PATH:?GitHub path export is required}"
