#!/usr/bin/env bash
deadline=${NAILBITER_SETUP_DEADLINE:-840}
if command -v setsid >/dev/null && command -v pkill >/dev/null && command -v pgrep >/dev/null; then
  setsid bash scripts/setup.sh &
  session=$!
  trap 'pkill -KILL -s "$session"; exit 143' TERM INT HUP
  (sleep "$deadline" && pkill -KILL -s "$session") 2>/dev/null &
  watchdog=$!
  wait "$session" 2>/dev/null
  status=$?
  pkill -P "$watchdog" sleep
  if wait "$watchdog"; then
    status=124
  fi
  pkill -KILL -s "$session"
  for _ in {1..50}; do
    pgrep -s "$session" >/dev/null || break
    sleep 0.1
  done
elif command -v timeout >/dev/null; then
  timeout -k 10 "$deadline" bash scripts/setup.sh
  status=$?
else
  bash scripts/setup.sh
  status=$?
fi
case $status in
  0) ;;
  124) printf '%s\n' "scripts/setup.sh did not finish before the provisioning deadline. The worktree is kept; rerun scripts/setup.sh." ;;
  *) printf '%s\n' "The worktree is kept so an agent can apply the fixes above and rerun scripts/setup.sh." ;;
esac
exit 0
