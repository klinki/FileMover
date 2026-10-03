#!/bin/sh
# Run inside the built image. These mocks test orchestration, not NAS compatibility.
set -eu

TEST_DIR="$(mktemp -d)"
mkdir -p "$TEST_DIR/bin" "$TEST_DIR/data" "$TEST_DIR/state"
export BN_ROOT_PATH="$TEST_DIR/data"
export BN_DB="$TEST_DIR/state/test.db"
export BN_BIN="$TEST_DIR/bin/fake-bn"
export TEST_CALLS="$TEST_DIR/calls"
export PATH="$TEST_DIR/bin:$PATH"

cat > "$TEST_DIR/bin/uname" <<'EOF'
#!/bin/sh
echo armv7l
EOF
cat > "$TEST_DIR/bin/getconf" <<'EOF'
#!/bin/sh
echo 32768
EOF
cat > "$BN_BIN" <<'EOF'
#!/bin/sh
echo "$1" >> "$TEST_CALLS"
if [ "$1" = "${TEST_FAIL_COMMAND:-}" ]; then
    echo "injected failure: $1" >&2
    exit 42
fi
EOF
chmod +x "$TEST_DIR/bin/"*

unset TEST_FAIL_COMMAND
/app/qnap-inventory.sh > "$TEST_DIR/output" 2>&1
cat > "$TEST_DIR/expected" <<'EOF'
db-test
scan-test
root
scan
hash
EOF
cmp "$TEST_DIR/expected" "$TEST_CALLS"
echo 'PASS: compatibility, registration, scan, and hash execute in order'

for step in db-test root scan hash; do
    export TEST_FAIL_COMMAND="$step"
    : > "$TEST_CALLS"
    if /app/qnap-inventory.sh > "$TEST_DIR/output" 2>&1; then
        echo "FAIL: $step failure was ignored" >&2
        exit 1
    fi
    case "$step" in
        db-test) ! grep -qx root "$TEST_CALLS" ;;
        root) ! grep -qx scan "$TEST_CALLS" ;;
        scan) ! grep -qx hash "$TEST_CALLS" ;;
    esac
    ! grep -q 'Inventory job finished' "$TEST_DIR/output"
    grep -q "injected failure: $step" "$TEST_DIR/output"
    echo "PASS: $step failure aborts the inventory job and appears in its log"
done

unset TEST_FAIL_COMMAND
export BN_ROOT_PATH="$TEST_DIR/nonexistent"
: > "$TEST_CALLS"
if /app/qnap-inventory.sh > "$TEST_DIR/output" 2>&1; then
    echo 'FAIL: nonexistent data directory was accepted' >&2
    exit 1
fi
[ ! -s "$TEST_CALLS" ]
echo 'PASS: nonexistent data directory aborts before running the CLI'
