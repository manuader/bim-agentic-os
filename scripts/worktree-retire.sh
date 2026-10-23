#!/usr/bin/env bash
# Retire git worktrees whose work is safely on the remote.
#
#   scripts/worktree-retire.sh [--push] [--dry-run] [--remote R] [--base B] <worktree>...
#   scripts/worktree-retire.sh [--dry-run] [--remote R] [--base B] --all
#
# A worktree is removed (and its local branch deleted) only when:
#   - it is not the main worktree and not on the base branch,
#   - it has no uncommitted, untracked, skip-worktree/assume-unchanged files,
#     and no ignored files other than regenerable build output
#     (bin/ obj/ node_modules/ __pycache__/ .pytest_cache/ *.pyc), and
#   - its HEAD is contained in some <remote>/* branch (after fetch --prune), or
#     every commit on it is already in <remote>/<base> by patch content
#     (squash/cherry-pick) and none of them is a merge commit.
# --push first pushes the worktree's branch to <remote> (never the base branch).
# --all also prunes metadata of worktrees whose directory no longer exists.
# Exit: 0 all requested worktrees retired (or would be, with --dry-run);
#       2 at least one was kept for safety; 1 usage or git error.
set -u
remote=origin base=main push=0 dry=0 all=0
paths=()
while [ $# -gt 0 ]; do
  case "$1" in
    --push) push=1 ;;
    --dry-run) dry=1 ;;
    --all) all=1 ;;
    --remote) remote="$2"; shift ;;
    --base) base="$2"; shift ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    -*) echo "unknown option $1" >&2; exit 1 ;;
    *) paths+=("$1") ;;
  esac
  shift
done
if [ $all -eq 0 ] && [ ${#paths[@]} -eq 0 ]; then echo "no worktree given (or --all)" >&2; exit 1; fi

main_wt="$(git worktree list --porcelain | sed -n '1s/^worktree //p')"
[ -n "$main_wt" ] || { echo "not inside a git repository" >&2; exit 1; }
git -C "$main_wt" fetch -q --prune "$remote" || { echo "fetch $remote failed; nothing retired" >&2; exit 1; }

if [ $all -eq 1 ]; then
  if [ $dry -eq 1 ]; then git -C "$main_wt" worktree prune -n -v; else git -C "$main_wt" worktree prune -v; fi
  while IFS= read -r line; do
    case "$line" in
      "worktree "*) p="${line#worktree }"; prunable=0 ;;
      prunable*) prunable=1 ;;
      "") [ -n "${p:-}" ] && [ "$p" != "$main_wt" ] && [ $prunable -eq 0 ] && paths+=("$p"); p="" ;;
    esac
  done < <(git -C "$main_wt" worktree list --porcelain; echo)
fi

backed_up() { # backed_up <worktree>
  local head; head="$(git -C "$1" rev-parse HEAD)" || return 1
  [ -n "$(git -C "$main_wt" for-each-ref --contains "$head" "refs/remotes/$remote/")" ] && return 0
  local b="refs/remotes/$remote/$base"
  git -C "$main_wt" rev-parse -q --verify "$b" >/dev/null || return 1
  [ -z "$(git -C "$main_wt" rev-list --merges "$b..$head")" ] || return 1
  ! git -C "$main_wt" cherry "$b" "$head" | grep -q '^+'
}

kept=0
for wt in ${paths[@]+"${paths[@]}"}; do
  arg="$wt"
  wt="$(cd "$arg" 2>/dev/null && git rev-parse --show-toplevel 2>/dev/null)" || { echo "KEEP $arg: not a worktree" >&2; kept=1; continue; }
  if [ "$wt" = "$main_wt" ]; then echo "KEEP $wt: main worktree is never removed" >&2; kept=1; continue; fi
  branch="$(git -C "$wt" symbolic-ref -q --short HEAD || true)"
  if [ "$branch" = "$base" ]; then echo "KEEP $wt: on base branch $base" >&2; kept=1; continue; fi
  if [ -n "$(git -C "$wt" status --porcelain --untracked-files=all)" ]; then
    echo "KEEP $wt: uncommitted or untracked files" >&2; kept=1; continue
  fi
  if git -C "$wt" ls-files -v | grep -q '^[a-zS]'; then
    echo "KEEP $wt: skip-worktree or assume-unchanged files" >&2; kept=1; continue
  fi
  ignored="$(git -C "$wt" status --porcelain --ignored=matching --untracked-files=all | sed -n 's/^!! //p' \
    | grep -v -E '(^|/)(bin|obj|node_modules|__pycache__|\.pytest_cache)/$|\.py[cod]$' || true)"
  if [ -n "$ignored" ]; then
    echo "KEEP $wt: ignored files that are not build output: $(echo "$ignored" | head -3 | tr '\n' ' ')" >&2; kept=1; continue
  fi
  if [ $push -eq 1 ]; then
    if [ -z "$branch" ]; then echo "KEEP $wt: detached HEAD, nothing to push" >&2; kept=1; continue; fi
    if [ $dry -eq 1 ]; then echo "WOULD PUSH $branch"
    else git -C "$wt" push -q -u "$remote" "$branch" || { echo "KEEP $wt: push failed" >&2; kept=1; continue; }
         git -C "$main_wt" fetch -q "$remote"; fi
  fi
  if ! backed_up "$wt"; then echo "KEEP $wt: commits not on $remote" >&2; kept=1; continue; fi
  if [ $dry -eq 1 ]; then echo "WOULD RETIRE $wt${branch:+ ($branch)}"; continue; fi
  git -C "$main_wt" worktree remove "$wt" || { echo "KEEP $wt: worktree remove failed" >&2; kept=1; continue; }
  [ -n "$branch" ] && git -C "$main_wt" branch -q -D "$branch"
  echo "RETIRED $wt${branch:+ ($branch)}"
done
[ $kept -eq 0 ] || exit 2
