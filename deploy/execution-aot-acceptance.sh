#!/bin/sh
# Replay only PC-prepared synthetic plans. Never mount existing NAS shares here.
set -eu
BN="${1:?CLI executable}"
FIXTURE="${2:?PC-prepared fixture directory}"
WORK="${3:?Fresh acceptance directory}"
[ ! -e "$WORK" ]
mkdir -p "$WORK/target" "$WORK/source" "$WORK/state" "$WORK/alternate"
cp -R "$FIXTURE/target/." "$WORK/target/"
cp -R "$FIXTURE/source/." "$WORK/source/"
cp "$FIXTURE/plans.db" "$WORK/state/plans.db"
ln -s link-source.txt "$WORK/target/link.txt"
DB="$WORK/state/plans.db"
if [ "${4:-}" = nas ]; then
    [ "$(uname -m)" = armv7l ]
    [ "$(getconf PAGESIZE)" = 32768 ]
fi
echo "Execution platform: $(uname -m), pages $(getconf PAGESIZE)"
"$BN" init --db "$WORK/state/new.db" --config "$WORK/state/config.json"
echo 'PASS: AOT database creation and schema helper'

expect_exit() {
    expected="$1"; shift
    if "$@"; then actual=0; else actual=$?; fi
    [ "$actual" -eq "$expected" ] || { echo "Expected exit $expected, got $actual"; exit 1; }
}
execute() {
    "$BN" execute "$1" --db "$DB" --target-path "$WORK/target" --source-path "$WORK/source" --yes ${2:-}
}
if printf 'n\n' | "$BN" execute main --db "$DB" --target-path "$WORK/target" --source-path "$WORK/source"; then
    echo 'FAIL: declined execution ran'; exit 1
else
    [ "$?" -eq 2 ]
fi
[ -f "$WORK/target/old/video.mp4" ]
[ ! -e "$WORK/target/album" ]
echo 'PASS: confirmation decline preserves files'
printf 'y\n' | "$BN" execute main --db "$DB" --target-path "$WORK/target" --source-path "$WORK/source"
[ ! -e "$WORK/target/old/video.mp4" ]
cmp "$WORK/target/album/video-from-vienna-č.mp4" "$FIXTURE/target/old/video.mp4"
cmp "$WORK/target/album/gallery-us-in-vienna.zip" "$FIXTURE/target/duplicate.zip"
cmp "$WORK/target/album/from-source.zip" "$FIXTURE/source/remote.zip"
[ ! -e "$WORK/target/duplicate.zip" ]
[ ! -e "$WORK/target/indexed-trash.zip" ]
cmp "$WORK/target/.backup-normalizer-trash/main/duplicate.zip" "$FIXTURE/target/duplicate.zip"
cmp "$WORK/target/.backup-normalizer-trash/main/indexed-trash.zip" "$FIXTURE/target/indexed-trash.zip"
cmp "$WORK/target/indexed-survivor.zip" "$FIXTURE/target/indexed-survivor.zip"
execute main --resume
expect_exit 2 "$BN" execute main --db "$DB" --target-path "$WORK/alternate" --source-path "$WORK/source" --yes --resume
echo 'PASS: moves, copies, verification, safe trash, reuse and root binding'
expect_exit 3 execute conflict --stop-on-error
cmp "$WORK/target/conflict-dest.txt" "$FIXTURE/target/conflict-dest.txt"
[ -f "$WORK/target/after-conflict.txt" ]
[ ! -e "$WORK/target/must-not-move.txt" ]
expect_exit 3 execute hash-conflict
cmp "$WORK/target/hash-source.txt" "$FIXTURE/target/hash-source.txt"
[ ! -e "$WORK/target/hash-dest.txt" ]
expect_exit 3 execute unsafe-trash
cmp "$WORK/target/unsafe-trash.zip" "$FIXTURE/target/unsafe-trash.zip"
expect_exit 3 execute failure
execute link
[ -L "$WORK/target/link.txt" ]
[ ! -e "$WORK/target/link-dest.txt" ]
echo 'PASS: conflicts, stop-on-error, failed operations and link guards'
expect_exit 3 execute recovery
cp "$FIXTURE/source/recovery.bin" "$WORK/target/recovery-dest.mp4"
execute recovery --resume
cmp "$WORK/target/recovery-dest.mp4" "$FIXTURE/source/recovery.bin"
echo 'PASS: recover move that landed before completion was journaled'
"$BN" db export --db "$DB" --output "$WORK/state/portable.db"
echo 'EXECUTION_BASE64_BEGIN'
base64 "$WORK/state/portable.db"
echo 'EXECUTION_BASE64_END'
echo 'RESULT: execution acceptance passed'
