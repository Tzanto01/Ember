#!/usr/bin/env bash
# Writes ~/.local/state/ember-ops/status.json for the ops dashboard (mounted read-only into ember-ops at /status):
# free space of every local disk, the newest pre-deploy dump in ~/backups/ember (metadata only), the deployed SHAs and the Ember
# container states. Run every minute by ops-status.timer. Needs jq.
set -euo pipefail

command -v jq >/dev/null || { echo "ops-status: jq is required" >&2; exit 1; }

out_dir="${HOME}/.local/state/ember-ops"
mkdir -p "$out_dir"

disks=$({ df -B1 -l --output=source,fstype,avail,size,target 2>/dev/null || true; } | tail -n +2 | awk '
  $1 ~ /^\/dev\// && $2 ~ /^(ext[234]|xfs|btrfs|f2fs)$/ {
    t = $0; for (i = 0; i < 4; i++) sub(/^ *[^ ]+ +/, "", t)
    if (t !~ /^\/(var\/lib\/docker|snap|boot)(\/|$)/ && !seen[$1]++) printf "%s\t%s\t%s\n", t, $3, $4
  }' | LC_ALL=C sort -t $'\t' -k1,1 | jq -R -s 'split("\n") | map(select(length > 0) | split("\t")
    | {mount: .[0], freeBytes: (.[1] | tonumber), totalBytes: (.[2] | tonumber)})')

backup=null
newest=$(find "${HOME}/backups/ember" -maxdepth 1 -type f -name 'emberdb-*.dump' -printf '%T@\t%s\t%f\n' 2>/dev/null | sort -rn | head -n 1 || true)
if [[ -n $newest ]]; then
  IFS=$'\t' read -r mtime size name <<<"$newest"
  backup=$(jq -n --arg name "$name" --arg at "$(date -u -d "@${mtime%.*}" +%Y-%m-%dT%H:%M:%SZ)" --argjson size "$size" \
    '{name: $name, at: $at, sizeBytes: $size}')
fi

sha() { [[ -r $1 ]] && head -n 1 "$1" | tr -d '\r\n' || true; }

if ps_out=$(docker ps -a --format '{{.Names}}\t{{.State}}\t{{.Status}}' 2>/dev/null); then missing=missing; else ps_out=""; missing=unknown; fi
containers=$(
  for name in ember-ember-1 ember-api-1 ember-staging-api-1 homeserver-postgres; do
    line=$(awk -F'\t' -v n="$name" '$1 == n' <<<"$ps_out")
    if [[ -z $line ]]; then
      printf '%s\t%s\t%s\n' "$name" "$missing" none
      continue
    fi
    IFS=$'\t' read -r _ state status <<<"$line"
    case $status in
      *"(healthy)"*) health=healthy ;;
      *"(unhealthy)"*) health=unhealthy ;;
      *"health: starting"*) health=starting ;;
      *) health=none ;;
    esac
    printf '%s\t%s\t%s\n' "$name" "$state" "$health"
  done | jq -R -s 'split("\n") | map(select(length > 0) | split("\t") | {name: .[0], state: .[1], health: .[2]})'
)

tmp=$(mktemp "${out_dir}/.status.XXXXXX")
trap 'rm -f "$tmp"' EXIT
jq -n \
  --arg writtenAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --argjson disks "$disks" \
  --argjson backup "$backup" \
  --arg production "$(sha "${HOME}/ember/DEPLOYED_SHA")" \
  --arg staging "$(sha "${HOME}/ember-staging/DEPLOYED_SHA")" \
  --argjson containers "$containers" \
  '{writtenAt: $writtenAt, disks: $disks, backup: $backup,
    deployed: {production: ($production | select(. != "") // null), staging: ($staging | select(. != "") // null)},
    containers: $containers}' >"$tmp"
mv -f "$tmp" "${out_dir}/status.json"
trap - EXIT
