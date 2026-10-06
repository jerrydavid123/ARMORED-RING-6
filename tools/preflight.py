"""Preflight: lay every sheet over the others and the vanilla params.

Reports unfilled cells, references that do not resolve, id collisions, and cells not yet verified in game.
Exit code 1 if any blocking problem (unfilled / unresolved / collision). Unverified cells are listed, not blocking.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from pq import load  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
SHEETS = os.path.join(HERE, "..", "sheets")


def sheet(name):
    with open(os.path.join(SHEETS, name + ".json"), encoding="utf-8") as f:
        return json.load(f)["rows"]


def main():
    problems, unverified = [], []
    frames, weapons, hooks = sheet("frames"), sheet("weapons"), sheet("hooks")
    wep_ids = {w["id"]: w for w in weapons}

    # 1. unfilled cells + verification state
    for name, rows in (("frames", frames), ("weapons", weapons), ("hooks", hooks)):
        for r in rows:
            for k, v in r.items():
                if v is None or v == "" or v == []:
                    problems.append(f"{name}:{r['id']}.{k} is unfilled")
            st = r.get("status", {})
            if not st.get("filled"):
                problems.append(f"{name}:{r['id']} status.filled is false")
            if not st.get("verified"):
                unverified.append(f"{name}:{r['id']}")

    # 2. references between sheets
    for f in frames:
        for k in ("start_weapon_right", "start_weapon_left", "start_ammo"):
            if f[k] not in wep_ids:
                problems.append(f"frames:{f['id']}.{k} -> '{f[k]}' not in weapons sheet")
        if f["level"] != sum(f["stats"].values()) - 79:
            problems.append(f"frames:{f['id']} level {f['level']} != sum(stats)-79 = {sum(f['stats'].values()) - 79}")
    for w in weapons:
        if "ammo" in w and w["ammo"] not in wep_ids:
            problems.append(f"weapons:{w['id']}.ammo -> '{w['ammo']}' not in weapons sheet")

    # 3. against the vanilla params
    chara, _ = load("CharaInitParam")
    weap, _ = load("EquipParamWeapon")
    seen = {}
    for f in frames:
        if f["class_row"] not in chara:
            problems.append(f"frames:{f['id']}.class_row {f['class_row']} not in CharaInitParam")
        if f["class_row"] in seen:
            problems.append(f"frames:{f['id']} reuses class_row {f['class_row']} of {seen[f['class_row']]}")
        seen[f["class_row"]] = f["id"]
    ids = {}
    for w in weapons:
        if w["base_row"] not in weap:
            problems.append(f"weapons:{w['id']}.base_row {w['base_row']} not in EquipParamWeapon")
        if w["param_row"] in weap:
            problems.append(f"weapons:{w['id']}.param_row {w['param_row']} collides with a vanilla EquipParamWeapon row")
        if w["param_row"] in ids:
            problems.append(f"weapons:{w['id']}.param_row {w['param_row']} collides with {ids[w['param_row']]}")
        ids[w["param_row"]] = w["id"]

    print("== PREFLIGHT ==")
    print(f"sheets: frames={len(frames)} weapons={len(weapons)} hooks={len(hooks)}")
    print(f"blocking problems: {len(problems)}")
    for p in problems:
        print("  BLOCK", p)
    print(f"unverified rows: {len(unverified)}")
    for u in unverified:
        print("  todo ", u)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
