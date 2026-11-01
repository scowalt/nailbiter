#!/usr/bin/env bash
set -euo pipefail
umask 077

if (( EUID != 0 )); then
  echo "Run this script as root." >&2
  exit 1
fi

readonly swapfile=/swapfile
readonly size=2147483648

exec 9>/run/nailbiter-swap.lock
flock -n 9

if awk '$1 == "/swapfile" && ($3 != "swap" || $4 ~ /(^|,)noauto(,|$)/) { found = 1 } END { exit !found }' /etc/fstab; then
  echo "Stop: /etc/fstab contains an incompatible entry for /swapfile." >&2
  exit 1
fi

if [[ -L "$swapfile" ]]; then
  echo "Stop: /swapfile is a symbolic link." >&2
  exit 1
elif [[ -e "$swapfile" ]]; then
  if [[ ! -f "$swapfile" || $(stat -c %u "$swapfile") != 0 || $(stat -c %s "$swapfile") != "$size" ]]; then
    echo "Stop: existing /swapfile must be a root-owned, 2 GiB regular file." >&2
    exit 1
  fi
  if [[ $(blkid -p -s TYPE -o value "$swapfile") != swap ]]; then
    echo "Stop: existing /swapfile has no swap signature. Do not overwrite it." >&2
    exit 1
  fi
else
  if [[ $(findmnt -n -o FSTYPE -T /) != ext4 ]]; then
    echo "Stop: this script supports an ext4 root filesystem only." >&2
    exit 1
  fi
  available=$(df -B1 --output=avail / | tail -1)
  if (( available < size + 1073741824 )); then
    echo "Stop: swap creation must leave at least 1 GiB of free disk space." >&2
    exit 1
  fi
  fallocate -l "$size" "$swapfile"
  chmod 600 "$swapfile"
  mkswap "$swapfile"
fi

chmod 600 "$swapfile"
if ! swapon --show=NAME --noheadings --raw | grep -Fxq "$swapfile"; then
  swapon "$swapfile"
fi

if ! awk '$1 == "/swapfile" && $3 == "swap" { found = 1 } END { exit !found }' /etc/fstab; then
  backup=$(mktemp /etc/fstab.before-nailbiter-swap.XXXXXX)
  cp -p /etc/fstab "$backup"
  printf '\n/swapfile none swap sw 0 0\n' >> /etc/fstab
  echo "Saved the original fstab at $backup"
fi

swapon --show
stat -c 'Swap file: %n size=%s owner=%U permissions=%a' "$swapfile"
