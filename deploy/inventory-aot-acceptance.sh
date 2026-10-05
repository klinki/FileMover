#!/bin/sh
# Synthetic data only. No NAS shares or existing inventories are accessed.
set -eu
BN=/app/BackupNormalizer
WORK=/tmp/bn-inventory
mkdir -p "$WORK/data/cache" "$WORK/state"
cp -R /app/inventory-fixture/. "$WORK/data/"
DB="$WORK/data/inventory.db"
echo "Architecture: $(uname -m)"
echo "Page size: $(getconf PAGESIZE)"
[ "$(uname -m)" = armv7l ]
[ "$(getconf PAGESIZE)" = 32768 ]
"$BN" --version
"$BN" db-test
echo 'PASS: database creation, writes and transactions'
"$BN" root add nas "$WORK/data" --db "$DB"
"$BN" root add nas "$WORK/data" --name updated --db "$DB"
"$BN" root list --db "$DB"
echo 'PASS: root insert, update and list'
ln -s hello.txt "$WORK/data/link.txt"
"$BN" scan nas --db "$DB" --mft off --usn off --no-progress --exclude-path-regex '^cache/'
"$BN" scan errors nas --db "$DB" --json
echo 'PASS: scan, exclusion, link metadata and diagnostics'
"$BN" hash --needed --db "$DB" --parallelism 2 --no-progress
"$BN" hash --needed --db "$DB" --parallelism 2 --no-progress > "$WORK/reuse.txt"
cat "$WORK/reuse.txt"
grep -q '0 hashed' "$WORK/reuse.txt"
"$BN" status nas --db "$DB" --json
echo 'PASS: hashes, cache reuse and inventory status'
printf 'modified content\n' > "$WORK/data/hello.txt"
mv "$WORK/data/album/video.mp4" "$WORK/data/video-moved.mp4"
rm "$WORK/data/remove.txt"
"$BN" scan nas --db "$DB" --mft off --usn off --no-progress
"$BN" hash --needed --db "$DB" --parallelism 2 --no-progress
"$BN" status nas --db "$DB" --json
"$BN" db export --db "$DB" --output "$WORK/export.db" --json
"$BN" status nas --db "$WORK/export.db" --json
echo 'PASS: rescan changes, stored exclusions and portable export'
cp /app/legacy.db "$WORK/state/legacy.db"
"$BN" root list --db "$WORK/state/legacy.db"
[ ! -e "$WORK/state/legacy.db.bn-migration.lock" ]
"$BN" root add added "$WORK/data" --db "$WORK/state/legacy.db"
find "$WORK/state" -name 'legacy.db.before-migration-*.db' | grep -q .
"$BN" root list --db "$WORK/state/legacy.db"
echo 'PASS: legacy read-only access and backed-up schema upgrade'
if /app/BackupNormalizer.Migrations --db "$WORK/state/mismatch.db" --manifest-hash invalid; then
    echo 'FAIL: mismatched helper package accepted'; exit 1
fi
[ ! -e "$WORK/state/mismatch.db" ]
echo 'PASS: mismatched migration package rejected before writes'
if "$BN" plan test --db "$WORK/state/must-not-exist.db"; then
    echo 'FAIL: unqualified planning command accepted'; exit 1
fi
[ ! -e "$WORK/state/must-not-exist.db" ]
echo 'PASS: unqualified commands rejected before database writes'
echo 'EXPORT_BASE64_BEGIN'
base64 "$WORK/export.db"
echo 'EXPORT_BASE64_END'
echo 'RESULT: inventory acceptance passed'
