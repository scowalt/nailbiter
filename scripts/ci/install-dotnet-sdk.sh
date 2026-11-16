#!/bin/sh
set -eu

sdk=${RUNNER_TEMP:?The runner temporary directory is required}/dotnet
download=$RUNNER_TEMP/dotnet-sdk-download
: "${GITHUB_PATH:?GitHub path export is required}"

version=$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)".*$/\1/p' apps/backend/global.json)
case $version in
  '' | *[!0-9.]*)
    echo '::error title=.NET SDK version unreadable::apps/backend/global.json must name the SDK as a plain version such as 10.0.401. Nothing was downloaded.'
    exit 1
    ;;
esac

archive=dotnet-sdk-$version-linux-x64.tar.gz
sha512=$(awk -v archive="$archive" '$2 == archive && NF == 2 { print $1 }' scripts/ci/dotnet-sdk.sha512)
case $sha512 in
  *[!0-9a-f]*) sha512='' ;;
esac
if [ "${#sha512}" -ne 128 ]; then
  echo "::error title=.NET SDK not pinned::scripts/ci/dotnet-sdk.sha512 needs one sha512 line for $archive, from Microsoft's releases.json. Nothing was downloaded."
  exit 1
fi

rm -rf "$sdk" "$download"
mkdir -p "$download"
curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 3 \
  --output "$download/$archive" "https://builds.dotnet.microsoft.com/dotnet/Sdk/$version/$archive"
if ! printf '%s  %s\n' "$sha512" "$archive" | (cd "$download" && sha512sum --check --strict --quiet); then
  rm -rf "$download"
  echo "::error title=.NET SDK checksum mismatch::$archive does not match scripts/ci/dotnet-sdk.sha512. Nothing was extracted."
  exit 1
fi
mkdir -p "$sdk"
tar -xzf "$download/$archive" -C "$sdk"
rm -rf "$download"
printf '%s\n' "$sdk" >> "$GITHUB_PATH"
