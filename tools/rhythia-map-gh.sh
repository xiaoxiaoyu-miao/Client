#!/data/data/com.termux/files/usr/bin/bash
# Pull Rhythia / Sound Space Plus maps from public GitHub repos using the gh CLI.
#
#   ./rhythia-map-gh.sh list                # list every map
#   ./rhythia-map-gh.sh list <keyword>      # filter by keyword
#   ./rhythia-map-gh.sh add <index|keyword|file name>
#   ./rhythia-map-gh.sh add --top 3 <keyword>
#   ./rhythia-map-gh.sh add --repo owner/repo <name>
#   ./rhythia-map-gh.sh import              # pull staged maps into the game (restart)
#
# Staged files land in the app's import folder and are imported on the next start.
set -uo pipefail

APP_PKG=net.rhythia.client
APP_UID=u0_a446
DST="/data/data/$APP_PKG/files/import"
WORK="$HOME/work/mapcache"
LEDGER="$WORK/staged.txt"
DEFAULT_REPO="supasolid/rhythia-maps"

mkdir -p "$WORK"
touch "$LEDGER"

die() { echo "error: $*" >&2; exit 1; }
need_gh() { command -v gh >/dev/null || die "gh CLI not found"; gh auth status >/dev/null 2>&1 || die "gh not logged in"; }

tree_file() { echo "$WORK/$(echo "$1" | tr '/' '_').json"; }

refresh_tree() {
  local repo="$1" out
  out="$(tree_file "$repo")"
  if [ ! -s "$out" ]; then
    echo "fetching file list for $repo ..." >&2
    gh api "repos/$repo/git/trees/HEAD?recursive=1" > "$out" || die "cannot list $repo"
  fi
  echo "$out"
}

list_maps() {
  local repo="$1" kw="${2:-}" out
  out="$(refresh_tree "$repo")"
  python3 - "$out" "$kw" <<'PY'
import json, sys
tree = json.load(open(sys.argv[1]))["tree"]
kw = (sys.argv[2] if len(sys.argv) > 2 else "").lower()
items = sorted(
    ((t["path"], t.get("size", 0)) for t in tree
     if t["type"] == "blob" and t["path"].endswith(".sspm")),
    key=lambda x: x[0].lower(),
)
hits = [it for it in items if kw in it[0].lower()]
print(f"{len(hits)} of {len(items)} maps" + (f" matching '{kw}'" if kw else ""))
for i, (path, size) in enumerate(hits, 1):
    print(f"{i:4d}  {size/1e6:6.2f} MB  {path}")
PY
}

resolve_one() {
  local repo="$1" want="$2" out
  out="$(refresh_tree "$repo")"
  python3 - "$out" "$want" <<'PY'
import json, sys
tree = json.load(open(sys.argv[1]))["tree"]
paths = sorted(t["path"] for t in tree if t["type"] == "blob" and t["path"].endswith(".sspm"))
want = sys.argv[2]
if want.isdigit() and 1 <= int(want) <= len(paths):
    print(paths[int(want) - 1]); raise SystemExit
for p in paths:
    if p == want:
        print(p); raise SystemExit
hits = [p for p in paths if want.lower() in p.lower()]
if not hits:
    raise SystemExit(f"no map matches {want!r}")
if len(hits) > 1:
    print("ambiguous, candidates:", *hits[:10], sep="\n  ", file=sys.stderr)
print(hits[0])
PY
}

stage() {
  local repo="$1" name="$2" slug out
  slug="$(echo "$name" | tr '/ ' '__')"
  out="$WORK/$slug"
  if grep -qxF "$name" "$LEDGER" 2>/dev/null; then
    echo "already staged earlier: $name (skipping)"
    return 0
  fi
  echo "downloading: $name"
  local enc
  enc="$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))' "$name")"
  gh api "repos/$repo/contents/$enc" -H "Accept: application/vnd.github.raw" > "$out" || {
    echo "download failed: $name" >&2; return 1; }
  [ -s "$out" ] || { echo "empty download: $name" >&2; return 1; }
  su -c "mkdir -p '$DST' && cp '$out' '$DST/' && chown $APP_UID:$APP_UID '$DST/$slug'" \
    || { echo "staging failed: $name" >&2; return 1; }
  echo "$name" >> "$LEDGER"
  echo "staged $(du -h "$out" | cut -f1): $slug"
}

restart_app() {
  su -c "am force-stop $APP_PKG" >/dev/null 2>&1
  su -c "am start -n $APP_PKG/com.godot.game.GodotAppLauncher" >/dev/null 2>&1
  echo "restarted $APP_PKG"
}

cmd="${1:-list}"
shift || true
repo="$DEFAULT_REPO"
if [ "${1:-}" = "--repo" ]; then repo="$2"; shift 2; fi

need_gh
case "$cmd" in
  list) list_maps "$repo" "${1:-}" ;;
  add)
    top=1
    if [ "${1:-}" = "--top" ]; then top="$2"; shift 2; fi
    want="${1:-}"; [ -n "$want" ] || die "usage: add <index|keyword|name>"
    out="$(refresh_tree "$repo")"
    mapfile -t picks < <(python3 - "$out" "$want" "$top" <<'PY'
import json, sys
tree = json.load(open(sys.argv[1]))["tree"]
paths = sorted(t["path"] for t in tree if t["type"] == "blob" and t["path"].endswith(".sspm"))
want, top = sys.argv[2], int(sys.argv[3])
if want.isdigit() and 1 <= int(want) <= len(paths):
    picked = [paths[int(want) - 1]]
else:
    exact = [p for p in paths if p == want]
    picked = exact or [p for p in paths if want.lower() in p.lower()]
for p in picked[:top]:
    print(p)
PY
)
    [ "${#picks[@]}" -gt 0 ] || die "no map matches '$want'"
    for p in "${picks[@]}"; do stage "$repo" "$p"; done
    restart_app ;;
  import) restart_app ;;
  *) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//' ;;
esac
