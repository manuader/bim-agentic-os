#!/bin/bash
# Progress of a Baton task board (tasks.md). Progress is computed from the board, never typed by hand.
#
#   progress.sh <tasks.md>            full report: totals, per phase, the next ready tasks
#   progress.sh <tasks.md> --line     only the one-line summary (for STATUS.md and the journal)
#   progress.sh <tasks.md> --update   rewrite the board's "## Progress" section with the full report
#
# The board is made of markdown tables whose header has a "Status" column; "### <phase>" headings group them.
# Each row's Status cell holds one mark:
#   ✅ done · 🟢 coded · 🔧 fixing · 🔄 in flight · ⏳ ready · ⛔ blocked · 🔒 owner · ⏭️ dropped
# Done = ✅. Dropped (⏭️) tasks leave the total. Rows without a known mark are ignored.

set -u
BOARD="${1:-}"; MODE="${2:-}"
[[ -z "$BOARD" ]] && { sed -n '2,12p' "$0"; exit 64; }
[[ -f "$BOARD" ]] || { echo "✗ $BOARD does not exist"; exit 64; }

report() {
  awk '
    function trim(s) { gsub(/^[ \t]+|[ \t]+$/, "", s); return s }
    function mark(s) {
      if (index(s, "✅")) return "done";    if (index(s, "🟢")) return "coded"
      if (index(s, "🔧")) return "fixing";  if (index(s, "🔄")) return "flight"
      if (index(s, "⏳")) return "ready";   if (index(s, "⛔")) return "blocked"
      if (index(s, "🔒")) return "owner";   if (index(s, "⏭")) return "dropped"
      return ""
    }
    BEGIN { phase = "(no phase)"; col = 0; skip = 0 }
    /^## / { skip = ($0 ~ /^## Progress/); phase = trim(substr($0, 4)); col = 0; next }
    skip { next }
    /^### / { phase = trim(substr($0, 5)); col = 0; next }
    /^\|/ {
      n = split($0, f, "|")
      if (col == 0) {
        for (i = 2; i < n; i++) if (tolower(trim(f[i])) == "status") col = i
        next
      }
      if ($0 ~ /^\|[ \t]*:?-+/) next
      m = mark(f[col]); if (m == "") next
      count[m]++
      if (!(phase in seen)) { seen[phase] = 1; order[++phases] = phase }
      if (m != "dropped") ptotal[phase]++
      if (m == "done") pdone[phase]++
      if (m == "ready" && nready < 8) ready[++nready] = trim(f[2])
      next
    }
    { col = (col && $0 ~ /^[ \t]*$/) ? 0 : col }
    END {
      total = 0; for (k in count) if (k != "dropped") total += count[k]
      done = count["done"] + 0
      pct = total ? 100 * done / total : 0
      printf "Progress: %d/%d done (%.1f %%) · 🔄 %d in flight · 🟢 %d coded · 🔧 %d fixing · ⏳ %d ready · ⛔ %d blocked · 🔒 %d owner · ⏭️ %d dropped\n", \
        done, total, pct, count["flight"], count["coded"], count["fixing"], count["ready"], count["blocked"], count["owner"], count["dropped"]
      if (mode == "line") exit
      if (phases > 1 || order[1] != "(no phase)") {
        print ""
        print "By phase:"
        for (i = 1; i <= phases; i++) {
          p = order[i]; t = ptotal[p] + 0; d = pdone[p] + 0
          printf "- %s: %d/%d (%.0f %%)\n", p, d, t, t ? 100 * d / t : 100
        }
      }
      if (nready) {
        line = ready[1]; for (i = 2; i <= nready; i++) line = line ", " ready[i]
        print ""
        print "Next ready: " line
      }
    }
  ' mode="$1" "$BOARD"
}

case "$MODE" in
  "")       report full ;;
  --line)   report line ;;
  --update)
    grep -q '^## Progress' "$BOARD" || { echo "✗ $BOARD has no '## Progress' section"; exit 65; }
    tmp=$(mktemp "${BOARD}.XXXXXX") || exit 4
    blockfile=$(mktemp) || exit 4
    report full > "$blockfile"
    awk -v blockfile="$blockfile" -v stamp="$(date '+%Y-%m-%d %H:%M')" '
      /^## Progress/ {
        print; print ""; print "<!-- computed by progress.sh, " stamp " — do not edit by hand -->"
        while ((getline l < blockfile) > 0) print l
        print ""; skip = 1; next
      }
      skip && /^## / { skip = 0 }
      !skip { print }
    ' "$BOARD" > "$tmp" && mv -f "$tmp" "$BOARD"
    rm -f "$blockfile"
    report line
    ;;
  *) sed -n '2,12p' "$0"; exit 64 ;;
esac
