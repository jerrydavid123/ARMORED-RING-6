"""Param query helper: python pq.py <ParamName> <id|from-to|all> [field,field,...]

Reads the unpacked vanilla XML in C:/Users/ddean/modtools/work/vanilla/regulation-bin.
"""
import re
import sys

ROOT = "C:/Users/ddean/modtools/work/vanilla/regulation-bin/"


def load(param):
    t = open(ROOT + param + ".param.xml", encoding="utf-8-sig").read()
    rows = {}
    for m in re.finditer(r'<row id="(-?\d+)"([^>]*?)/>', t):
        rows[int(m.group(1))] = dict(re.findall(r'(\w+)="([^"]*)"', m.group(2)))
    fields = {}
    for m in re.finditer(r'<field name="(\w+)" type="(\w+)"[^>]*?defaultValue="([^"]*)"', t):
        fields[m.group(1)] = (m.group(2), m.group(3))
    return rows, fields


if __name__ == "__main__":
    param, sel = sys.argv[1], sys.argv[2]
    want = sys.argv[3].split(",") if len(sys.argv) > 3 else None
    rows, fields = load(param)
    if sel == "all":
        ids = sorted(rows)
    elif "-" in sel[1:]:
        a, b = sel.split("-")
        ids = [i for i in sorted(rows) if int(a) <= i <= int(b)]
    else:
        ids = [int(sel)]
    for i in ids:
        r = rows.get(i)
        if r is None:
            print(i, "MISSING")
            continue
        full = {k: r.get(k, fields[k][1]) for k in fields}
        if want:
            print(i, {k: full.get(k) for k in want})
        else:
            print(i, {k: v for k, v in full.items() if v not in ("0", "-1", "0.0", "1")})
