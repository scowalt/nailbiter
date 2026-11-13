#!/usr/bin/env bash
set -uo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root" || exit 1
caller_path=$PATH
failures=0
warnings=0
pins=0
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MISE_AUTO_INSTALL=0

report() {
  printf '%-5s %s\n' "$1" "$2"
  if [[ -n ${3:-} ]]; then printf '      fix: %s\n' "$3"; fi
  case $1 in
    fail) failures=$((failures + 1)) ;;
    warn) warnings=$((warnings + 1)) ;;
  esac
}

verdict() {
  if ((failures > 0)); then
    printf 'NOT READY: %d problem(s). Apply each fix above, then rerun scripts/setup.sh.\n' "$failures"
    exit 1
  fi
  printf 'READY (%d warning(s))\n' "$warnings"
  exit 0
}

mentions_version() {
  [[ " $1 " =~ [^0-9.]${2//./\\.}([^0-9.]|\.[0-9]) ]]
}

version_at_least() {
  [[ $(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n1) == "$2" ]]
}

pinned_tools() {
  awk -v q="'" '
    { sub(/\r$/, ""); sub(/[ \t]*#.*/, ""); sub(/^[ \t]+/, ""); sub(/[ \t]+$/, ""); gsub(q, "\"") }
    /^\[tools\./ { print $0, "?" }
    /^\[/ { in_tools = ($0 == "[tools]"); next }
    !in_tools || !NF { next }
    match($0, /^("[^"]+"|[^ \t=".]+)[ \t]*=[ \t]*"[0-9][0-9.]*"$/) { gsub(/[" \t]/, ""); sub(/=/, " "); print; next }
    { sub(/[ \t]*=.*/, ""); gsub(/"/, ""); print $0, "?" }
  ' .mise.toml
}

version_line() {
  (cd apps/backend && PATH=$1 timeout -k 5 20 "$2" --version 2>/dev/null | sed -n 1p)
}

pin_satisfied() {
  if [[ $1 == dotnet ]]; then
    [[ ${2%.*} == "${3%.*}" ]] && version_at_least "$2" "$3"
  else
    mentions_version "$2" "$3"
  fi
}

check_tool() {
  local tool=$1 pin=$2 label=$3 found bare version
  found=$(command -v "$tool") || {
    report fail "$label is not installed" "mise install"
    return
  }
  if ! version=$(version_line "$PATH" "$tool") || ! pin_satisfied "$tool" "$version" "$pin"; then
    report fail "$found does not provide $label" "mise install"
    return
  fi
  report ok "$label"
  bare=$(PATH=$caller_path command -v "$tool")
  if [[ -z $bare ]] || ! version=$(version_line "$caller_path" "$tool") || ! pin_satisfied "$tool" "$version" "$pin"; then
    report warn "'$tool' in your own shell is ${bare:-missing}, not $label" \
      "run it as 'mise exec -- $tool ...', or activate mise in your shell profile so its tools come first"
  fi
}

check_dotnet_sdk() {
  local pin=$1 required
  required=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' apps/backend/global.json)
  if [[ $pin != "$required" ]]; then
    report fail ".mise.toml pins .NET SDK $pin but apps/backend/global.json requires $required" \
      "make the two pins equal"
    return
  fi
  check_tool dotnet "$required" ".NET SDK $required (apps/backend/global.json)"
}

run_step() {
  local label=$1 log
  shift
  if log=$(timeout -k 10 150 "$@" 2>&1); then
    report ok "$label"
  else
    printf '%s\n' "$log" | tail -n 5 | sed 's/^/      | /'
    report fail "$label" "rerun scripts/setup.sh; if it fails again, run '$*' and read its output"
  fi
}

install_git_hook() {
  local hook
  hook="$(git rev-parse --git-path hooks)/pre-commit"
  if grep -qs '^call_lefthook()' "$hook"; then
    report ok "git pre-commit hook (lefthook)"
  elif [[ -e $hook || -L $hook ]]; then
    report warn "$hook is not Lefthook's, so setup left it alone and this repository's pre-commit checks do not run" \
      "run 'mise exec -- bun node_modules/.bin/lefthook run pre-commit' before each commit, or merge the hook into lefthook.yml"
  else
    run_step "git pre-commit hook installed (lefthook)" bun node_modules/.bin/lefthook install pre-commit
  fi
}

check_playwright() {
  local launch="const { chromium } = require('playwright'); await (await chromium.launch()).close();"
  if ((dependency_failures > 0)); then
    report skip "Playwright Chromium until the bun dependencies install"
  elif timeout -k 10 60 bun -e "$launch" >/dev/null 2>&1; then
    report ok "Playwright Chromium"
  else
    report warn "Playwright Chromium does not launch, so browser regressions (bun run test:live-sync) cannot run" \
      "mise exec -- bunx playwright install chromium"
  fi
}

printf 'nailbiter setup: %s\n' "$root"

if ! command -v timeout >/dev/null; then
  report fail "GNU timeout is not on PATH" \
    "setup runs on Linux; elsewhere install GNU coreutils and put its unprefixed commands first on PATH"
  verdict
fi
if ! command -v mise >/dev/null; then
  report fail "mise is not on PATH" "install mise (https://mise.jdx.dev/getting-started.html), then rerun scripts/setup.sh"
  verdict
fi
if ! mise_env=$(timeout -k 5 60 mise env -s bash 2>/dev/null); then
  timeout -k 5 60 mise env -s bash 2>&1 | tail -n 5 | sed 's/^/      | /'
  report fail "mise could not load .mise.toml" \
    "if mise says the config is not trusted, review .mise.toml and run 'mise trust'"
  verdict
fi
eval "$mise_env"

while read -r tool pin; do
  pins=$((pins + 1))
  if [[ $pin == '?' ]]; then
    report fail "cannot read the '$tool' pin in .mise.toml" 'write each pin as name = "1.2.3" under [tools]'
    continue
  fi
  case $tool in
    *dotnet*) check_dotnet_sdk "$pin" ;;
    *:*) report warn "setup cannot check '$tool' because the pin does not name its command" "check it with 'mise ls --current'" ;;
    *) check_tool "$tool" "$pin" "$tool $pin" ;;
  esac
done < <(pinned_tools)
if ((pins == 0)); then
  report fail ".mise.toml pins no tools under [tools]" 'restore the [tools] table, one name = "1.2.3" pin per line'
fi

if ((failures > 0)); then
  printf 'skip  dependencies, git hook and restore until the toolchain is fixed\n'
  verdict
fi

before=$failures
for project in . apps/web-components apps/www; do
  run_step "bun dependencies ($project)" bun install --cwd "$project" --frozen-lockfile --ignore-scripts
done
dependency_failures=$((failures - before))
install_git_hook
for project in apps/backend/backend.csproj apps/backend.fsharp.tests/backend.fsharp.tests.fsproj apps/backend.tests/backend.tests.csproj; do
  name=${project##*/}
  run_step "dotnet restore (${name%.*})" dotnet restore "$project"
done
check_playwright
verdict
