#!/usr/bin/env python3
"""Fail the build when line coverage of a package drops below its threshold.

Usage: coverage-gate.py <cobertura.xml> <Package>=<min-percent> [...]
"""
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    root = ET.parse(sys.argv[1]).getroot()
    rates = {p.get("name"): float(p.get("line-rate", "0")) * 100 for p in root.iter("package")}
    failed = False
    for rule in sys.argv[2:]:
        name, minimum = rule.split("=")
        actual = rates.get(name)
        if actual is None:
            print(f"::error::coverage report has no package {name} (found: {', '.join(sorted(rates))})")
            failed = True
            continue
        status = "OK " if actual >= float(minimum) else "LOW"
        print(f"{status} {name}: {actual:.1f}% line coverage (minimum {minimum}%)")
        failed |= actual < float(minimum)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
