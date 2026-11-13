#!/usr/bin/env bash
timeout -k 10 "${NAILBITER_SETUP_DEADLINE:-840}" bash scripts/setup.sh
case $? in
  0) ;;
  124 | 137) printf '%s\n' "scripts/setup.sh did not finish before the provisioning deadline. The worktree is kept; rerun scripts/setup.sh." ;;
  *) printf '%s\n' "The worktree is kept so an agent can apply the fixes above and rerun scripts/setup.sh." ;;
esac
exit 0
