#!/usr/bin/env bash
# Release-day check: does Lookout hold every report, status, note and picture Ember has? Read-only on both sides.
# Usage: tools/feedback-move/check.sh <lookout checkout>
# Env: what Lookout's tools/board.sh needs (LOOKOUT_AGENT_NAME, LOOKOUT_AGENT_PROVIDER); it runs with LOOKOUT_AGENT_APP=ember.
# Exit codes: see compare.py. The exports hold no texts of Ember's reports, but Lookout's do: they stay in a private directory
# that is deleted at the end.
set -euo pipefail
if [ "$#" -ne 1 ] || [ ! -x "$1/tools/board.sh" ]; then
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
if [ "$(db emberops <<<"SELECT to_regclass('public.comments') IS NOT NULL")" = t ]; then
  db emberops <<<"SELECT coalesce(json_object_agg(id, n), '{}') FROM (SELECT feedback_id AS id, count(*) AS n FROM comments GROUP BY 1) c" >"$work/notes.json"
else
  echo '{}' >"$work/notes.json"
fi

for status in open planned implemented released declined; do
  "$board" list ember "$status" 2>"$work/list.err" >"$work/list.json"
  if grep -q 'cut off' "$work/list.err"; then
    echo "check: more than 1000 $status tickets, the list is cut off" >&2
    exit 1
  fi
  jq -r '.[] | select(.source == "ingest" or .kind == "feedback") | .id' "$work/list.json" >>"$work/ids"
done
while read -r id; do
  "$board" show "$id" 2>"$work/show.err" </dev/null || { cat "$work/show.err" >&2; exit 1; }
done <"$work/ids" | jq -s . >"$work/lookout.json"

python3 "$here/compare.py" "$work/ember.json" "$work/notes.json" "$work/lookout.json"
