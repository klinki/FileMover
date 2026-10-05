"""Independently check replay journals and values written by generated EF queries."""
import sqlite3


def verify_database(path, target, source):
    with sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True) as db:
        assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert db.execute("PRAGMA foreign_key_check").fetchall() == []
        assert db.execute("SELECT COUNT(*) FROM __EFMigrationsHistory").fetchone()[0] == 6
        expected = {"main": ["Completed"] * 8 + ["Skipped"],
                    "conflict": ["Conflict", "Planned"], "hash-conflict": ["Conflict"],
                    "unsafe-trash": ["Conflict"], "failure": ["Failed"],
                    "link": ["Skipped"], "recovery": ["Completed"]}
        for plan, statuses in expected.items():
            rows = db.execute("SELECT Status, StartedUtc, CompletedUtc, Error, SkipReason "
                              "FROM PlanOperation WHERE PlanId=? ORDER BY Sequence", (plan,)).fetchall()
            assert [row[0] for row in rows] == statuses, (plan, rows)
            for status, started, completed, error, reason in rows:
                if status == "Planned":
                    assert started is None and completed is None and error is None
                else:
                    assert completed and "T" in completed, (plan, completed)
                    if status != "Skipped":
                        assert started and "T" in started, (plan, started)
                    if status in ("Failed", "Conflict"):
                        assert error, (plan, error)
                    else:
                        assert error is None
                    if status == "Skipped":
                        assert reason
            if plan == "main":
                assert rows[-1][4] == "PC excluded link"
            if plan == "conflict":
                assert "destination exists" in rows[0][3]
            if plan == "hash-conflict":
                assert "hash mismatch" in rows[0][3]
            if plan == "unsafe-trash":
                assert "no surviving verified copy" in rows[0][3]
            row = db.execute("SELECT Status, ExecutionSourceRootPath, ExecutionTargetRootPath "
                             "FROM Plan WHERE Id=?", (plan,)).fetchone()
            state = "Partial" if plan in ("conflict", "hash-conflict", "unsafe-trash", "failure") else "Completed"
            assert row == (state, source if plan == "main" else None, target), (plan, row)
        logs = db.execute("SELECT Level, Message, TimestampUtc FROM ExecutionLog").fetchall()
        assert {row[0] for row in logs} == {"INFO", "WARN", "ERROR"}
        assert all(message and timestamp and "T" in timestamp for _, message, timestamp in logs)
        assert any("MKDIR" in message for _, message, _ in logs)
        assert any("MOVE ok" in message for _, message, _ in logs)
        assert any("no surviving verified copy" in message for _, message, _ in logs)
        return {"plansChecked": len(expected), "operationsChecked": sum(map(len, expected.values())),
                "journalRows": len(logs), "integrity": "ok", "foreignKeyViolations": 0}
