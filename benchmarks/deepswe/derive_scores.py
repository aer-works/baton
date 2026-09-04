"""Derive sortable scores from a DeepSWE selected-configurations snapshot.

The raw `selected-configurations.csv` in a date directory is an immutable capture and sorts only by
pass@1. This writes `derived-scores.csv` beside it with:

- quality_per_100_steps, quality_per_usd: plain ratios.
- on_frontier / on_vendor_frontier: Pareto flags on quality vs steps, across all vendors and within one.
- utility_lambda_<L>: quality - L x steps; L is the --lambda argument and is named in the header.

What each column is for, and why a ratio alone misleads, is written once in benchmarks/README.md
("Derived scores"); --sweep prints the top rows under several L values instead of writing anything.

Usage:
    python benchmarks/deepswe/derive_scores.py benchmarks/deepswe/2026-09-04 [--lambda 0.10] [--sweep]
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

RAW = "selected-configurations.csv"
OUT = "derived-scores.csv"
DEFAULT_LAMBDA = 0.10
SWEEP = (0.0, 0.05, 0.10, 0.20, 0.40)


def load(date_dir: Path) -> list[dict]:
    with (date_dir / RAW).open(encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    for r in rows:
        r["_q"] = int(r["pass_at_1_percent"])
        r["_s"] = int(r["agent_steps"])
        r["_c"] = float(r["avg_api_cost_usd"])
    return rows


def dominated(row: dict, others: list[dict]) -> bool:
    return any(
        o is not row
        and o["_q"] >= row["_q"]
        and o["_s"] <= row["_s"]
        and (o["_q"] > row["_q"] or o["_s"] < row["_s"])
        for o in others
    )


def derive(rows: list[dict], lam: float) -> list[dict]:
    col = f"utility_lambda_{lam:.2f}"
    out = []
    for r in rows:
        same_vendor = [o for o in rows if o["vendor"] == r["vendor"]]
        d = {k: v for k, v in r.items() if not k.startswith("_")}
        d["quality_per_100_steps"] = f"{r['_q'] / r['_s'] * 100:.1f}"
        d["quality_per_usd"] = f"{r['_q'] / r['_c']:.1f}" if r["_c"] > 0 else ""
        d["on_frontier"] = "true" if not dominated(r, rows) else "false"
        d["on_vendor_frontier"] = "true" if not dominated(r, same_vendor) else "false"
        d[col] = f"{r['_q'] - lam * r['_s']:.1f}"
        d["_u"] = r["_q"] - lam * r["_s"]
        out.append(d)
    out.sort(key=lambda d: (d["on_vendor_frontier"] != "true", -d["_u"], -int(d["pass_at_1_percent"])))
    for d in out:
        del d["_u"]
    return out


def sweep(rows: list[dict], top: int = 6) -> None:
    for lam in SWEEP:
        ranked = sorted(rows, key=lambda r: r["_q"] - lam * r["_s"], reverse=True)[:top]
        print(f"lambda={lam:.2f}  (quality points forfeited per agent step)")
        for r in ranked:
            print(f"  {r['_q'] - lam * r['_s']:6.1f}  {r['model']:18s} {r['effort']:6s} q={r['_q']:3d} steps={r['_s']:3d}")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("date_dir", type=Path)
    ap.add_argument("--lambda", dest="lam", type=float, default=DEFAULT_LAMBDA)
    ap.add_argument("--sweep", action="store_true", help="print top rows across several lambdas; write nothing")
    ap.add_argument("--check", action="store_true", help="exit 1 if the committed derived file differs from a fresh derivation")
    a = ap.parse_args(argv)

    rows = load(a.date_dir)
    if a.sweep:
        sweep(rows)
        return 0

    derived = derive(rows, a.lam)
    fields = list(derived[0].keys())
    target = a.date_dir / OUT
    if a.check:
        import io

        buf = io.StringIO()
        w = csv.DictWriter(buf, fieldnames=fields, lineterminator="\n")
        w.writeheader()
        w.writerows(derived)
        # newline="" so CRLF drift is a difference, not something universal-newline reading hides.
        current = target.open(encoding="utf-8", newline="").read() if target.exists() else ""
        if current != buf.getvalue():
            print(f"derive_scores: {target} is stale; rerun without --check", file=sys.stderr)
            return 1
        print(f"derive_scores: {target} is current")
        return 0

    with target.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields, lineterminator="\n")
        w.writeheader()
        w.writerows(derived)
    print(f"derive_scores: wrote {target} ({len(derived)} rows, lambda={a.lam:.2f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
