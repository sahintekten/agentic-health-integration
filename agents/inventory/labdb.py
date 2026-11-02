"""Inventory for System B (PostgreSQL): schema, column stats, duplicates and join keys."""

import random
from collections import Counter

import psycopg

from agents.inventory.values import profile

MAX_ROWS = 50000


def _norm(value):
    return value.strip() if isinstance(value, str) else value


def _duplicates(columns, rows, unique_cols):
    rest = [i for i, c in enumerate(columns) if c not in unique_cols]
    if not rest:
        return None

    def groups(idx):
        counts = Counter(tuple(_norm(r[i]) for i in idx) for r in rows)
        return sum(1 for n in counts.values() if n > 1)

    base = groups(rest)
    result = {"ignoring_columns": sorted(unique_cols), "duplicate_groups": base}
    best = None
    # only try dropping low-cardinality columns (like an audit user); dropping a
    # timestamp or a value column would just find rows that match by chance
    for i in rest:
        if len({r[i] for r in rows}) > 20:
            continue
        n = groups([j for j in rest if j != i])
        if n > max(base * 2, base + 5) and (best is None or n > best[1]):
            best = (columns[i], n)
    if best:
        result["also_ignoring"] = {"column": best[0], "duplicate_groups": best[1]}
    return result


def build(settings, sample_rows=5):
    with psycopg.connect(**settings, connect_timeout=10) as conn:
        tables = [
            r[0]
            for r in conn.execute(
                "SELECT table_name FROM information_schema.tables "
                "WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1"
            )
        ]
        col_rows = conn.execute(
            "SELECT table_name, column_name, data_type, character_maximum_length, is_nullable "
            "FROM information_schema.columns WHERE table_schema = 'public' "
            "ORDER BY table_name, ordinal_position"
        ).fetchall()
        constraints = conn.execute(
            "SELECT tc.constraint_type, kcu.table_name, kcu.column_name "
            "FROM information_schema.table_constraints tc "
            "JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name "
            "WHERE tc.table_schema = 'public'"
        ).fetchall()
        comments = conn.execute(
            "SELECT count(*) FROM pg_description d JOIN pg_class c ON c.oid = d.objoid "
            "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public'"
        ).fetchone()[0]

        inventory = {"system": "b", "kind": "postgresql", "tables": {}, "fields": {}}
        values_by_col = {}
        for table in tables:
            cur = conn.execute(f'SELECT * FROM "{table}" LIMIT {MAX_ROWS}')
            columns = [d.name for d in cur.description]
            rows = cur.fetchall()
            row_count = conn.execute(f'SELECT count(*) FROM "{table}"').fetchone()[0]
            meta = {r[1]: r for r in col_rows if r[0] == table}
            unique_cols = set()
            for i, col in enumerate(columns):
                values = [r[i] for r in rows]
                values_by_col[(table, col)] = {v for v in values if v is not None}
                p = profile(values)
                if p["distinct"] == len(rows) and p["null_rate"] == 0:
                    unique_cols.add(col)
                m = meta[col]
                inventory["fields"][f"{table}.{col}"] = {
                    "table": table,
                    "db_type": m[2] + (f"({m[3]})" if m[3] else ""),
                    "nullable": m[4] == "YES",
                    "unique": col in unique_cols,
                    **p,
                }
            rng = random.Random(0)
            picked = rows[:sample_rows] + rng.sample(rows, min(sample_rows, len(rows)))
            inventory["tables"][table] = {
                "row_count": row_count,
                "columns": columns,
                "sample_rows": [dict(zip(columns, [_norm(v) for v in r])) for r in picked],
                "duplicates": _duplicates(columns, rows, unique_cols),
            }

    joins = []
    by_name = {}
    for table, col in values_by_col:
        by_name.setdefault(col, []).append(table)
    for col, owners in by_name.items():
        for a in owners:
            for b in owners:
                va, vb = values_by_col[(a, col)], values_by_col[(b, col)]
                if a != b and va:
                    missing = sorted(map(str, va - vb))
                    joins.append(
                        {
                            "from": f"{a}.{col}",
                            "to": f"{b}.{col}",
                            "share_of_values_found": round(1 - len(missing) / len(va), 3),
                            "values_not_found": missing[:5],
                        }
                    )

    inventory["schema_notes"] = {
        "primary_keys": [f"{t}.{c}" for k, t, c in constraints if k == "PRIMARY KEY"],
        "foreign_keys": [f"{t}.{c}" for k, t, c in constraints if k == "FOREIGN KEY"],
        "comments": comments,
    }
    inventory["join_candidates"] = joins
    return inventory
