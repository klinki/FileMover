#!/bin/sh
# QNAP TS-431P3 acceptance test (spec §39). Run INSIDE the qnap-arm32 container
# on the actual NAS before trusting any scan:
#
#   ./qnap-check.sh [./BackupNormalizer] [/data]
#
# Checks: ARMv7 CPU, 32 KiB page size, SQLite create/insert/read/txn roundtrip,
# read-only scan smoke test. Exits non-zero on the first failure.
set -eu

BN="${1:-./BackupNormalizer}"
DATA="${2:-/data}"
FAIL=0

check() {
    desc="$1"; shift
    if "$@" >/tmp/qnap-check.log 2>&1; then
        echo "PASS: $desc"
    else
        echo "FAIL: $desc (see /tmp/qnap-check.log)"
        FAIL=1
    fi
}

echo "== platform =="
ARCH="$(uname -m)"
PAGESIZE="$(getconf PAGESIZE)"
echo "arch=$ARCH pagesize=$PAGESIZE"
[ "$ARCH" = "armv7l" ] || { echo "FAIL: expected armv7l, got $ARCH"; FAIL=1; }
[ "$PAGESIZE" = "32768" ] || { echo "FAIL: expected 32768, got $PAGESIZE"; FAIL=1; }

echo "== app =="
check "db-test (SQLite roundtrip + txn commit/rollback)" "$BN" db-test
check "scan-test $DATA" "$BN" scan-test "$DATA"

if [ "$FAIL" -eq 0 ]; then
    echo "ALL CHECKS PASSED — NAS scanner supported."
else
    echo "CHECKS FAILED — do not trust scans from this host."
fi
exit "$FAIL"
