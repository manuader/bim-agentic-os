#!/bin/bash
# The relay LOCK: only one session works at a time.
#
#   lock.sh create    <LOCK>                               # FREE, if it doesn't exist
#   lock.sh status    <LOCK>                               # exit 0 free · 1 taken and alive · 2 taken and stale
#   lock.sh take      <LOCK> "<taken_by>" <session> [--stale]
#   lock.sh beat      <LOCK> <session>                     # one heartbeat
#   lock.sh beat-loop <LOCK> <session> [minutes]           # background heartbeat; ends by itself once the LOCK is lost
#   lock.sh release   <LOCK> <session>
#
# <session> is the sessionId from get_session("self") (the app's local_…): the link to the chat is built from it.
# A TAKEN lock whose last heartbeat is BATON_STALE_MIN minutes old or more (45 by default) is stale, but stale is not
# dead: before taking it with --stale, check the holder session with get_session.
# Exit codes of take, beat and release: 0 ok · 1 held by a live session · 2 stale without --stale · 3 not yours.

set -u
STALE_MIN="${BATON_STALE_MIN:-45}"

now() { date '+%Y-%m-%d %H:%M:%S %z'; }

to_epoch() {
  date -j -f '%Y-%m-%d %H:%M:%S %z' "$1" +%s 2>/dev/null || date -d "$1" +%s 2>/dev/null || echo ""
}

field() { sed -n "s/^$2: //p" "$1" 2>/dev/null | head -1; }

write_atomic() {
  local target="$1" tmp
  tmp=$(mktemp "${target}.XXXXXX") || exit 4
  cat > "$tmp" && mv -f "$tmp" "$target"
}

MUTEX=""
with_mutex() {
  local tries=0
  MUTEX="$1.mutex"
  until mkdir "$MUTEX" 2>/dev/null; do
    tries=$((tries + 1))
    if [[ $tries -gt 50 ]]; then
      # A mutex older than a minute is orphaned: operations under the mutex take milliseconds.
      if [[ -n "$(find "$MUTEX" -maxdepth 0 -mmin +1 2>/dev/null)" ]]; then
        rmdir "$MUTEX" 2>/dev/null; tries=0; continue
      fi
      echo "✗ could not take the mutex $MUTEX" >&2; exit 4
    fi
    sleep 0.2
  done
  trap 'rmdir "$MUTEX" 2>/dev/null' EXIT
}

minutes_since_beat() {
  local beat epoch
  beat=$(field "$1" last_heartbeat)
  epoch=$(to_epoch "$beat")
  [[ -z "$epoch" ]] && { echo ""; return; }
  echo $(( ($(date +%s) - epoch) / 60 ))
}

# 0 free · 1 taken and alive · 2 taken and stale
situation() {
  local lock="$1" min
  [[ "$(field "$lock" state)" != "TAKEN" ]] && return 0
  min=$(minutes_since_beat "$lock")
  [[ -z "$min" ]] && return 2
  [[ $min -ge $STALE_MIN ]] && return 2
  return 1
}

cmd="${1:-}"; lock="${2:-}"
[[ -z "$cmd" || -z "$lock" ]] && { sed -n '2,15p' "$0"; exit 64; }

case "$cmd" in
  create)
    [[ -f "$lock" ]] && { echo "= already exists: $lock"; exit 0; }
    mkdir -p "$(dirname "$lock")"
    printf 'state: FREE\ncreated_at: %s\n' "$(now)" | write_atomic "$lock"
    echo "✓ FREE lock at $lock"
    ;;

  status)
    [[ -f "$lock" ]] || { echo "✗ $lock does not exist"; exit 64; }
    cat "$lock"
    situation "$lock"; s=$?
    min=$(minutes_since_beat "$lock")
    case $s in
      0) echo "→ FREE: the LOCK side of the gate is open" ;;
      1) echo "→ TAKEN and alive (last heartbeat ${min} min ago): gate CLOSED" ;;
      2) echo "→ TAKEN but stale (last heartbeat ${min:-?} min ago ≥ ${STALE_MIN}): check with get_session(<session>) that its lastActivityAt is also past ${STALE_MIN} min before taking it" ;;
    esac
    exit $s
    ;;

  take)
    by="${3:-}"; session="${4:-}"; flag="${5:-}"
    [[ -z "$by" || -z "$session" ]] && { echo "usage: lock.sh take <LOCK> \"<taken_by>\" <session> [--stale]"; exit 64; }
    with_mutex "$lock"
    [[ -f "$lock" ]] || printf 'state: FREE\n' | write_atomic "$lock"
    situation "$lock"; s=$?
    holder=$(field "$lock" session)
    if [[ $s -eq 1 && "$holder" != "$session" ]]; then
      echo "✗ held by $(field "$lock" taken_by) ($holder), alive"; exit 1
    fi
    if [[ $s -eq 2 && "$holder" != "$session" && "$flag" != "--stale" ]]; then
      echo "✗ stale but unverified: confirm with get_session that $holder is dead, then repeat with --stale"; exit 2
    fi
    previous=""
    [[ $s -eq 2 && "$holder" != "$session" ]] && previous="taken_over_from: $(field "$lock" taken_by) ($holder)"
    t=$(now)
    {
      echo "state: TAKEN"
      echo "taken_by: $by"
      echo "session: $session"
      echo "link: claude://claude.ai/epitaxy/$session"
      echo "taken_at: $t"
      echo "last_heartbeat: $t"
      [[ -n "$previous" ]] && echo "$previous"
    } | write_atomic "$lock"
    [[ "$(field "$lock" session)" == "$session" ]] || { echo "✗ the write did not stick"; exit 4; }
    echo "✓ LOCK taken by $by ($session) — $t"
    ;;

  beat)
    session="${3:-}"
    with_mutex "$lock"
    [[ "$(field "$lock" state)" == "TAKEN" && "$(field "$lock" session)" == "$session" ]] || exit 3
    sed "s/^last_heartbeat: .*/last_heartbeat: $(now)/" "$lock" | write_atomic "$lock"
    ;;

  beat-loop)
    session="${3:-}"; every="${4:-10}"
    while true; do
      bash "$0" beat "$lock" "$session" || {
        if [[ "$(field "$lock" state)" == "FREE" ]]; then echo "heartbeat: LOCK released, stopping"; exit 0; fi
        echo "heartbeat: the LOCK no longer belongs to $session — stop dispatching and check"; exit 3
      }
      sleep $((every * 60))
    done
    ;;

  release)
    session="${3:-}"
    with_mutex "$lock"
    if [[ "$(field "$lock" session)" != "$session" ]]; then
      echo "✗ the LOCK isn't held by $session (holder: $(field "$lock" session)): not releasing"; exit 3
    fi
    {
      echo "state: FREE"
      echo "last_holder: $(field "$lock" taken_by)"
      echo "last_session: $session"
      echo "released_at: $(now)"
    } | write_atomic "$lock"
    echo "✓ LOCK FREE"
    ;;

  *)
    sed -n '2,15p' "$0"; exit 64
    ;;
esac
