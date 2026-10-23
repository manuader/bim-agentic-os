#!/usr/bin/env bash
# Oracle for scripts/worktree-retire.sh: a worktree is removed only when every
# commit on it is already on the remote (by ancestry or by patch content) and
# it has no uncommitted or untracked files. Run: bash tests/baton/worktree-retire.test.sh
set -u
here="$(cd "$(dirname "$0")/../.." && pwd)"
script="$here/scripts/worktree-retire.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
count=0
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { count=$((count + 1)); }
[ -f "$script" ] || fail "missing $script (intentional TDD red)"

export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@example.invalid GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@example.invalid
git init -q --bare "$tmp/origin.git"
git init -q -b main "$tmp/repo"
cd "$tmp/repo"
git remote add origin "$tmp/origin.git"
echo base > a.txt; git add a.txt; git commit -qm base; git push -q origin main

mk() { # mk <name> : worktree on branch <name> with one commit
  git worktree add -q -b "$1" "$tmp/wt-$1" main
  echo "$1" > "$tmp/wt-$1/$1.txt"
  git -C "$tmp/wt-$1" add "$1.txt"; git -C "$tmp/wt-$1" commit -qm "feat: $1"
}

# 1. unpushed commit -> kept
mk unpushed
bash "$script" "$tmp/wt-unpushed" >/dev/null 2>&1; rc=$?
[ $rc -eq 2 ] || fail "unpushed: expected exit 2, got $rc"; ok
[ -d "$tmp/wt-unpushed" ] || fail "unpushed worktree must stay"; ok
git rev-parse -q --verify refs/heads/unpushed >/dev/null || fail "unpushed branch must stay"; ok

# 2. pushed branch -> removed with its local branch
mk pushed; git push -q origin pushed
bash "$script" "$tmp/wt-pushed" >/dev/null || fail "pushed: expected exit 0"; ok
[ ! -d "$tmp/wt-pushed" ] || fail "pushed worktree must be removed"; ok
! git rev-parse -q --verify refs/heads/pushed >/dev/null || fail "pushed local branch must be deleted"; ok

# 3. pushed but dirty (tracked edit) -> kept
mk dirty; git push -q origin dirty; echo edit >> "$tmp/wt-dirty/dirty.txt"
bash "$script" "$tmp/wt-dirty" >/dev/null 2>&1; [ $? -eq 2 ] || fail "dirty must exit 2"; ok
[ -d "$tmp/wt-dirty" ] || fail "dirty worktree must stay"; ok

# 4. pushed but untracked file -> kept
mk untracked; git push -q origin untracked; echo new > "$tmp/wt-untracked/new.txt"
bash "$script" "$tmp/wt-untracked" >/dev/null 2>&1; [ $? -eq 2 ] || fail "untracked must exit 2"; ok
[ -f "$tmp/wt-untracked/new.txt" ] || fail "untracked file must survive"; ok

# 5. squashed/cherry-picked into origin/main with another hash -> removed
mk squashed
git cherry-pick squashed >/dev/null; git push -q origin main
bash "$script" "$tmp/wt-squashed" >/dev/null || fail "squashed content in main must retire"; ok
[ ! -d "$tmp/wt-squashed" ] || fail "squashed worktree must be removed"; ok

# 6. --push publishes the branch, then retires it
mk topush
bash "$script" --push "$tmp/wt-topush" >/dev/null || fail "--push must publish and retire"; ok
git ls-remote --exit-code origin refs/heads/topush >/dev/null || fail "--push must create remote branch"; ok
[ ! -d "$tmp/wt-topush" ] || fail "--push worktree must be removed"; ok

# 7. --push refuses the base branch; main worktree is never removed
bash "$script" --push "$tmp/repo" >/dev/null 2>&1; [ $? -ne 0 ] || fail "main worktree must be refused"; ok
[ -f "$tmp/repo/a.txt" ] || fail "main worktree must stay"; ok

# 8. --dry-run removes nothing
mk dry; git push -q origin dry
bash "$script" --dry-run "$tmp/wt-dry" >/dev/null || fail "dry-run of backed-up worktree exits 0"; ok
[ -d "$tmp/wt-dry" ] || fail "dry-run must not remove"; ok

# 9. --all retires backed-up ones, keeps the rest, prunes missing directories
mk gone; git push -q origin gone; mv "$tmp/wt-gone" "$tmp/moved-away"
bash "$script" --all >/dev/null 2>&1; rc=$?
[ $rc -eq 2 ] || fail "--all with kept worktrees exits 2, got $rc"; ok
[ ! -d "$tmp/wt-dry" ] || fail "--all must retire backed-up dry"; ok
[ -d "$tmp/wt-unpushed" ] && [ -d "$tmp/wt-dirty" ] && [ -d "$tmp/wt-untracked" ] || fail "--all must keep unsafe ones"; ok
! git worktree list --porcelain | grep -q "wt-gone" || fail "--all must prune missing worktree metadata"; ok
git rev-parse -q --verify refs/heads/unpushed >/dev/null || fail "--all must keep unpushed branch"; ok

# 10. remote branch deleted after push (stale tracking ref) -> kept
mk stale; git push -q origin stale; git -C "$tmp/origin.git" branch -q -D stale
bash "$script" "$tmp/wt-stale" >/dev/null 2>&1; [ $? -eq 2 ] || fail "stale tracking ref must not count as backup"; ok
[ -d "$tmp/wt-stale" ] || fail "stale worktree must stay"; ok

# 11. merge commit with its own content, picked piecewise into main -> kept
git worktree add -q -b evil "$tmp/wt-evil" main
git -C "$tmp/wt-evil" checkout -q -b side; echo s > "$tmp/wt-evil/s.txt"; git -C "$tmp/wt-evil" add s.txt; git -C "$tmp/wt-evil" commit -qm side
git -C "$tmp/wt-evil" checkout -q evil; echo e1 > "$tmp/wt-evil/e1.txt"; git -C "$tmp/wt-evil" add e1.txt; git -C "$tmp/wt-evil" commit -qm e1
git -C "$tmp/wt-evil" merge -q --no-ff --no-commit side; echo x > "$tmp/wt-evil/evil.txt"; git -C "$tmp/wt-evil" add evil.txt; git -C "$tmp/wt-evil" commit -qm merge
git cherry-pick side evil~1 >/dev/null; git push -q origin main
bash "$script" "$tmp/wt-evil" >/dev/null 2>&1; [ $? -eq 2 ] || fail "unpushed merge commit must keep worktree"; ok

# 12. skip-worktree edit hidden from status -> kept
mk skip; git push -q origin skip; git -C "$tmp/wt-skip" update-index --skip-worktree skip.txt; echo hidden >> "$tmp/wt-skip/skip.txt"
bash "$script" "$tmp/wt-skip" >/dev/null 2>&1; [ $? -eq 2 ] || fail "skip-worktree files must keep worktree"; ok

# 13. ignored files: build output allowed, anything else (e.g. .env, .bim/) kept
mk ign; printf '*.env\nbin/\n' > "$tmp/wt-ign/.gitignore"; git -C "$tmp/wt-ign" add .gitignore; git -C "$tmp/wt-ign" commit -qm ignore; git push -q origin ign
mkdir -p "$tmp/wt-ign/bin"; echo b > "$tmp/wt-ign/bin/out.dll"; echo secret > "$tmp/wt-ign/local.env"
bash "$script" "$tmp/wt-ign" >/dev/null 2>&1; [ $? -eq 2 ] || fail "ignored non-build file must keep worktree"; ok
mv "$tmp/wt-ign/local.env" "$tmp/saved.env"
bash "$script" "$tmp/wt-ign" >/dev/null 2>&1 || fail "only build output ignored must retire"; ok
[ ! -d "$tmp/wt-ign" ] || fail "ign worktree must be removed"; ok

# 14. a local branch named like the remote ref must not fake a backup
mk shadow; git branch origin/main shadow
bash "$script" "$tmp/wt-shadow" >/dev/null 2>&1; [ $? -eq 2 ] || fail "local origin/main branch must not count"; ok
git branch -q -D origin/main

# 15. messages name the argument; dry-run --all exits like a real run would
out="$(bash "$script" "$tmp/no-such-dir" 2>&1)"; case "$out" in *no-such-dir*) ok ;; *) fail "KEEP message must name the path: $out" ;; esac
mk dry2; git push -q origin dry2; mv "$tmp/wt-dry2" "$tmp/moved2"
out="$(bash "$script" --dry-run --all 2>&1)"; case "$out" in *KEEP*moved2*|*KEEP*wt-dry2*) fail "dry-run must not report prunable entries as KEEP" ;; *) ok ;; esac

# 16. ignored secret inside a tracked bin/ directory -> kept
mk tbin; mkdir -p "$tmp/wt-tbin/bin"; echo run > "$tmp/wt-tbin/bin/run"; printf '*.env\n' > "$tmp/wt-tbin/.gitignore"
git -C "$tmp/wt-tbin" add bin/run .gitignore; git -C "$tmp/wt-tbin" commit -qm tbin; git push -q origin tbin
echo secret > "$tmp/wt-tbin/bin/prod.env"
bash "$script" "$tmp/wt-tbin" >/dev/null 2>&1; [ $? -eq 2 ] || fail "ignored file in tracked bin/ must keep worktree"; ok
[ -f "$tmp/wt-tbin/bin/prod.env" ] || fail "secret in tracked bin/ must survive"; ok

echo "worktree-retire: $count assertions passed"
