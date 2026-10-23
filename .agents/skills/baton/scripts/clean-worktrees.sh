#!/bin/bash
# Removes worktrees that are no longer needed, without losing anything.
#
#   clean-worktrees.sh <repo>                    # dry run: what it would remove and keep, and why
#   clean-worktrees.sh <repo> --apply            # remove
#   clean-worktrees.sh <repo> --apply <path>     # remove only that one (when integrating an agent's task)
#
# Environment:
#   KEEP="final release"   worktree or branch names never removed (always include the work branch)
#   REMOTE=origin          the remote every commit must be on before removal
#
# A worktree is removed only if all four hold:
#   1. it is not locked (agent in flight) and no process has its cwd inside it;
#   2. it has no uncommitted changes and no untracked, non-ignored files;
#   3. its commit is on the remote (some remote branch contains it). If it isn't and the worktree has a branch,
#      --apply pushes it first and removes it only once the push is confirmed;
#   4. it is not in KEEP.
# Then its local branch is deleted with `branch -d` (never -D), and finally `git worktree prune`. Never --force.

set -u
R="${1:-}"
[[ -z "$R" || ! -d "$R/.git" && ! -f "$R/.git" ]] && { sed -n '2,8p' "$0"; exit 64; }
KEEP="${KEEP:-}"
REMOTE="${REMOTE:-origin}"
APPLY=0; ONLY=""
[[ "${2:-}" == "--apply" ]] && APPLY=1 && ONLY="${3:-}"

git -C "$R" fetch -q "$REMOTE" 2>/dev/null || { echo "✗ could not fetch $REMOTE: removing nothing without seeing it"; exit 1; }
busy=$(lsof -d cwd 2>/dev/null | awk '{print $NF}' | grep -F "$R/" | sort -u)

removed=0; kept=0; freed_kb=0
while IFS=$'\t' read -r path head branch locked; do
  [[ "$path" == "$R" ]] && continue
  name=$(basename "$path")
  [[ -n "$ONLY" && "$path" != "$ONLY" && "$name" != "$ONLY" ]] && continue
  why=""
  for keep in $KEEP; do [[ "$name" == "$keep" || "$branch" == "$keep" ]] && why="protected"; done
  [[ -z "$why" && "$locked" == "1" ]] && why="locked (agent in flight)"
  [[ -z "$why" && -n "$busy" ]] && echo "$busy" | grep -qF "$path" && why="processes running inside"
  if [[ -z "$why" ]]; then
    dirty=$(git -C "$path" status --porcelain 2>/dev/null | grep -v '__pycache__' | head -3)
    [[ -n "$dirty" ]] && why="uncommitted changes: $(echo $dirty | cut -c1-80)"
  fi
  if [[ -z "$why" ]] && ! git -C "$R" branch -r --contains "$head" 2>/dev/null | grep -q "$REMOTE/"; then
    if [[ -n "$branch" && $APPLY == 1 ]]; then
      git -C "$R" push -q "$REMOTE" "$branch" 2>/dev/null && git -C "$R" fetch -q "$REMOTE" \
        && git -C "$R" branch -r --contains "$head" | grep -q "$REMOTE/" || why="not on $REMOTE and the push failed"
    elif [[ -n "$branch" ]]; then
      echo "  ↑ $name: would push it to $REMOTE/$branch before removing it"
    else
      why="detached HEAD not on $REMOTE"
    fi
  fi
  if [[ -n "$why" ]]; then
    echo "  = keeping $name — $why"; kept=$((kept + 1)); continue
  fi
  kb=$(du -sk "$path" 2>/dev/null | cut -f1); kb=${kb:-0}
  if [[ $APPLY == 1 ]]; then
    if git -C "$R" worktree remove "$path" 2>/dev/null; then
      [[ -n "$branch" ]] && git -C "$R" branch -d -q "$branch" 2>/dev/null
      echo "  ✓ removed $name ($((kb / 1024)) MB)"; removed=$((removed + 1)); freed_kb=$((freed_kb + kb))
    else
      echo "  = keeping $name — git worktree remove refused"; kept=$((kept + 1))
    fi
  else
    echo "  - would remove $name ($((kb / 1024)) MB) — clean, unused, on $REMOTE"
    removed=$((removed + 1)); freed_kb=$((freed_kb + kb))
  fi
done < <(git -C "$R" worktree list --porcelain | awk '
  /^worktree /{p=substr($0,10); h=""; b=""; l=0}
  /^HEAD /{h=$2} /^branch /{b=substr($2,12)} /^locked/{l=1}
  /^$/{print p "\t" h "\t" b "\t" l}')
[[ $APPLY == 1 ]] && git -C "$R" worktree prune
verb=$([[ $APPLY == 1 ]] && echo "removed" || echo "to remove")
echo "$verb: $removed ($((freed_kb / 1024)) MB) · kept: $kept"
