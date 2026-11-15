#!/bin/sh
set -eu

revision=${GITHUB_SHA:?The commit the checker is built from is required}
rm -rf .ci/listing-checker
mkdir -p .ci
dotnet publish apps/listing-check/listing-check.csproj --configuration Release --runtime linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none --output .ci/listing-checker
tar -C .ci -cf .ci/listing-checker.tar listing-checker
sha256sum .ci/listing-checker.tar > .ci/listing-checker.sha256
printf '%s\n' "$revision" > .ci/listing-checker.commit
