#!/usr/bin/env bash
# Release-day check: does Lookout hold every report, status, note and picture Ember has? Read-only on both sides.
# Usage: tools/feedback-move/check.sh <lookout checkout> [<ids of reports settled by hand, comma-separated>]
# Env: what Lookout's tools/board.sh needs (LOOKOUT_AGENT_NAME, LOOKOUT_AGENT_PROVIDER); it runs with LOOKOUT_AGENT_APP=ember.
# Exit codes: see compare.py. The exports hold no texts of Ember's reports, but Lookout's do: they stay in a private directory
# that is deleted at the end.
set -euo pipefail
if [ "$#" -lt 1 ] || [ "$#" -gt 2 ] || [ ! -x "$1/tools/board.sh" ]; then
  sed -n '3,4p' "$0" >&2
  exit 1
fi
board="$1/tools/board.sh"
here=$(cd "$(dirname "$0")" && pwd)
export LOOKOUT_AGENT_APP=ember
work=$(mktemp -d -p "${XDG_RUNTIME_DIR:-/tmp}")
trap 'rm -rf "$work"' EXIT

db() { docker exec -i homeserver-postgres psql -U finvarex -d "$1" -At -v ON_ERROR_STOP=1 -f -; }

db emberdb <"$here/export.sql" >"$work/ember.json"
db emberops >"$work/notes.json" <<'SQL'
SELECT coalesce(json_object_agg(id, notes), '{}')
FROM (SELECT feedback_id AS id, json_agg(md5(btrim(body, E' \t\r\n'))) AS notes FROM comments GROUP BY 1) c;
SQL

: >"$work/ids"
for status in open planned implemented released declined; do
  "$board" list ember "$status" 2>"$work/board.err" >"$work/list.json" || { cat "$work/board.err" >&2; exit 1; }
  if grep -q 'cut off' "$work/board.err"; then
    echo "check: more $status tickets than one list holds" >&2
    exit 1
  fi
  jq -r '.[] | select(.source == "ingest") | .id' "$work/list.json" >>"$work/ids"
done
while read -r id; do
  "$board" show "$id" 2>"$work/board.err" </dev/null || { cat "$work/board.err" >&2; exit 1; }
done <"$work/ids" | jq -s . >"$work/lookout.json"

python3 "$here/compare.py" "$work/ember.json" "$work/notes.json" "$work/lookout.json" ${2:+"$2"}
