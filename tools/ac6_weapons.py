"""Derive each weapon's real AC6 numbers into sheets/weapon_stats.json (read by build_regulation.py and the DLL generator).

Reads the AC6 param dumps in work/ac6mv (made with `Ac6Convert paramdump`): EquipParamWeapon, BehaviorParam_PC,
Bullet, AtkParam_Pc. Chain: weapon.behaviorVariationId V -> BehaviorParam_PC rows 100000000 + V*1000 + judge
(judge 0 = the normal shot/slash, 10 = the second slash, 100 = the charged one) -> refId = Bullet row (projectile) or
AtkParam row (hit). Projectile: speed, range, radius, HitBulletID (the impact atk). Damage = the atk row's
atkPhysCorrection / atkMagCorrection percent of the weapon's attackBasePhysics / attackBaseMagic.
Lengths and speeds scale by K (frame size / AC size), like the movement numbers.
"""
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
DUMPS = "C:/Users/ddean/modtools/work/ac6mv"
K = 0.3


def load(name):
    rows, cur = {}, None
    for line in open(os.path.join(DUMPS, name + ".txt"), encoding="utf-8", errors="replace"):
        m = re.match(r"-- row (-?\d+)", line)
        if m:
            cur = int(m.group(1))
            rows[cur] = {}
            continue
        m = re.match(r"\s+(\w+) = (.*)", line)
        if m and cur is not None:
            rows[cur][m.group(1)] = m.group(2).strip()
    return rows


def num(r, k, d=0.0):
    try:
        return float(r.get(k, d))
    except ValueError:
        return d


def main():
    W, B, Bu, A = load("EquipParamWeapon"), load("BehaviorParam_PC"), load("Bullet"), load("AtkParam_Pc")
    sheet = json.load(open(os.path.join(HERE, "..", "sheets", "weapons.json"), encoding="utf-8"))
    out = {}
    for row in sheet["rows"]:
        if "ac6_weapon_row" not in row:
            continue
        w = W[row["ac6_weapon_row"]]
        base_phys, base_mag = num(w, "attackBasePhysics"), num(w, "attackBaseMagic")
        V = int(w["behaviorVariationId"])
        res = dict(ac6_weapon_row=row["ac6_weapon_row"], base_phys=base_phys, base_mag=base_mag, weight=num(w, "weight"),
                   magazine=num(w, "bulletNum"), total_rounds=num(w, "totalBulletNum"), consume_en=num(w, "consumeEN"),
                   lock_range_m=round(num(w, "lockRange") * K, 1), attacks={})
        for judge, label in ((0, "normal"), (10, "second"), (100, "charged")):
            b = B.get(100000000 + V * 1000 + judge)
            if not b:
                continue
            ref = int(b["refId"])
            a = dict(reload_s=num(b, "reloadTimeSecond"), rounds=max(1, int(num(b, "consumeBulletNum"))), shots=int(num(b, "numShoot", 1)),
                     charge_s=num(b, "chargeDelayTimeSec"), heat=num(b, "heat_AddValue"))
            bullet = Bu.get(ref)
            atk = A.get(ref)
            if bullet:
                a.update(speed_mps=round(num(bullet, "initVellocity") * K, 2), max_speed_mps=round(max(num(bullet, "maxVellocity"), num(bullet, "initVellocity")) * K, 2), homing_deg=num(bullet, "homingAngle"), range_m=round(num(bullet, "lifeMoveDist", num(bullet, "dist")) * K, 1),
                         radius_m=round(max(0.15, num(bullet, "hitRadius") * K), 2))
                hit = int(num(bullet, "HitBulletID", -1))
                if hit in A:
                    atk = A[hit]
                elif int(num(bullet, "atkId_Bullet", -1)) in A:
                    atk = A[int(bullet["atkId_Bullet"])]
            if atk:
                a.update(phys_corr=num(atk, "atkPhysCorrection"), mag_corr=num(atk, "atkMagCorrection"), impact=num(atk, "impactPower"),
                         knockback_mps=round(num(atk, "knockbackMPS") * K, 1))
                a["damage_phys"] = round(base_phys * a["phys_corr"] / 100.0, 1)
                a["damage_mag"] = round(base_mag * a["mag_corr"] / 100.0, 1)
            res["attacks"][label] = a
        out[row["id"]] = res
        print(row["id"], json.dumps(res)[:600])
    json.dump(out, open(os.path.join(HERE, "..", "sheets", "weapon_stats.json"), "w", encoding="utf-8"), indent=2)


if __name__ == "__main__":
    main()
