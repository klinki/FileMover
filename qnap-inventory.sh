#!/bin/sh
# One inventory job. Data is mounted read-only; the database lives in /state.
set -eu

BN="${BN_BIN:-/app/BackupNormalizer}"
ROOT_ID="${BN_ROOT_ID:-nas}"
DATA="${BN_ROOT_PATH:-/data}"
DB="${BN_DB:-/state/nas.db}"
PARALLELISM="${BN_HASH_PARALLELISM:-1}"

[ -d "$DATA" ] || { echo "Data directory does not exist: $DATA" >&2; exit 2; }
mkdir -p "$(dirname "$DB")"

echo "Checking NAS compatibility before inventorying $DATA"
"$(dirname "$0")/qnap-check.sh" "$BN" "$DATA"

echo "Registering root $ROOT_ID in $DB"
"$BN" root add "$ROOT_ID" "$DATA" --db "$DB"
echo "Scanning $DATA"
"$BN" scan "$ROOT_ID" --db "$DB" --mft off
echo "Hashing uncached files, parallelism=$PARALLELISM"
"$BN" hash "$ROOT_ID" --db "$DB" --parallelism "$PARALLELISM"
echo "Inventory job finished. Database: $DB"
