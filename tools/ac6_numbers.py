"""Fill the movement columns of sheets/frames.json from the real AC6 params (read from the player's own AC6 install).

Run after dumping the AC6 params with the converter:
  Ac6Convert paramdump <ER Game dir> <AC6 X.param> <Paramdex AC6 Defs X.xml>  > work/ac6mv/X.txt
(see MODLOG; the dumps used are MovementAcTypeParam, EquipParamBooster, EquipParamGenerator, EquipParamProtector,
TentativePlayerParam). AC6 units are km/h, m/s^2 and EN points in AC6 metres; our frame is smaller than an AC, so
lengths scale by K (speeds and distances x K, accelerations x K, times unchanged, EN becomes a percent of the pool).

  python ac6_numbers.py
"""
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
SHEET = os.path.join(HERE, "..", "sheets", "frames.json")
DUMPS = "C:/Users/ddean/modtools/work/ac6mv"

# which real parts stand in for each frame (leg = the kit's own legs; generator = same maker family, closest class)
PICK = {
    "frame_light": dict(leg=53080100, gen=65030000, gen_name="IA-C01G AORTA (Ayre's own)", booster=60070100),
    "frame_medium": dict(leg=53000000, gen=65010200, gen_name="VP-20S (Balam class)"),
    "frame_heavy": dict(leg=53010000, gen=65010100, gen_name="DF-GN-06 MING-TANG (Dafeng)"),
}
BOOSTER = 60000000  # the stock booster of the starter AC; every frame uses it so frames differ by legs and generator
MOVE_ROW = 0        # MovementAcTypeParam row for bipedal legs (100 = reverse joint, 200/210 = tetrapod, 300+ = tank)
QB_PROFILE = 0.8    # average of the QB speed ramp relative to peak (estimate; AC6 does not store distance)


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


def f(r, k):
    return float(r[k])


def main():
    move = load("MovementAcTypeParam")[MOVE_ROW]
    boosters = load("EquipParamBooster")
    gens = load("EquipParamGenerator")
    prot = load("EquipParamProtector")
    sysp = load("TentativePlayerParam")[0]
    sheet = json.load(open(SHEET, encoding="utf-8"))
    kmh = lambda v: v / 3.6
    for row in sheet["rows"]:
        K = 0.15 * float(row["size_scale"])  # AC ~ 10.7 m tall -> kit is 0.15 x that, doubled by size_scale
        bid = PICK[row["id"]].get("booster", BOOSTER)
        boost = boosters[bid]
        leg = prot[PICK[row["id"]]["leg"]]
        gen = gens[PICK[row["id"]]["gen"]]
        pool = f(gen, "energyMax")
        pct = lambda en: round(en / pool * 100.0, 2)
        qb_peak = f(move, "quickBoostMaxSpeedKMPH") + f(leg, "groundQbAddSpeedKMH")
        qb_dur = f(boost, "QB_AccelTimeF30") / 30.0
        new = dict(
            ac6_k=K,
            ac6_sources=dict(
                movement=f"MovementAcTypeParam row {MOVE_ROW} (biped)", booster=f"EquipParamBooster {bid}",
                generator=f"EquipParamGenerator {PICK[row['id']]['gen']} {PICK[row['id']]['gen_name']}", legs=f"EquipParamProtector {PICK[row['id']]['leg']}",
                physics="TentativePlayerParam row 0", en_pool=pool,
            ),
            # EN (percent of the generator's pool)
            qb_en_pct=pct(f(boost, "consumeFixedEN_QB")),
            qb_cooldown_s=f(boost, "QB_ReloadTimeSec"),
            qb_duration_s=round(qb_dur, 3),
            qb_distance_m=round(kmh(qb_peak) * qb_dur * QB_PROFILE * K, 2),
            boost_en_pct_per_s=pct(f(boost, "consumeEN")),
            vboost_en_pct_per_s=pct(f(boost, "consumeEN_BoostUp")),
            en_regen_pct_per_s=pct(f(gen, "energyRecoveryPerSec")),
            en_regen_delay_s=f(gen, "energyRecoveryDelayTimeSec"),
            en_empty_delay_s=f(gen, "energyRecoveryDelayTimeForEmptySec"),
            en_empty_restore_pct=pct(f(gen, "energyRecoverValForEmpty")),
            # boost on the ground / in the air (booster) and the brake when the stick is released (movement row)
            ground_boost_mps=round(kmh(f(boost, "f00_dashBoostMaxSpeedKMPH")) * K, 2),
            ground_boost_accel=round(f(boost, "f02_dashBoostMaxAccelMPSS") * K, 1),
            air_boost_mps=round(kmh(f(boost, "f06_flyBoostHorizontalMaxSpeedKMPH")) * K, 2),
            air_boost_accel=round(f(boost, "f08_flyBoostHorizontalMaxAccelMPSS") * K, 1),
            ground_brake_mps2=round(f(move, "movementToAnimMoveBrakeMPSS") * K, 1),
            landing_brake_mps2=round(f(move, "landingHorizontalSlowBrakeMPSS") * f(leg, "landBrakeScale") * K, 1),
            # vertical boost (hold jump): climb, and the slow horizontal speed that goes with it
            vboost_up_mps=round(kmh(f(boost, "upperBoostMaxSpeedKMPH")) * K, 2),
            vboost_up_accel=round(f(boost, "upperBoostMaxAccelMPSS") * K, 1),
            vboost_h_mps=round(kmh(f(boost, "upperBoostHorizontalMaxSpeedKMPH")) * K, 2),
            vboost_h_accel=round(f(boost, "upperBoostHorizontalAccelMPSS") * K, 1),
            # airborne without thrust
            air_h_mps=round(kmh(f(move, "jumpHorizontalMaxSpeedKMPH")) * K, 2),
            air_h_accel=round(f(move, "jumpHorizontalAccelMPSS") * K, 1),
            fly_h_brake_mps2=round(f(move, "flyHorizontalBrakeMPSS") * K, 1),
            fly_up_brake_mps2=round(f(move, "flyUpBrakeMPSS") * K, 1),
            gravity_mps2=round(f(sysp, "MovementGravity") * K, 1),
            fall_max_mps=round(kmh(f(sysp, "FallMaxSpeedKMH")) * K, 1),
            hop_alt_m=round(f(leg, "jumpSpecifyAlt_GoalAlt") * K, 1),
            overheat_recover_pct=0.0,
        )
        row.update(new)
        for old in ("boost_distance_m", "boost_duration_s", "boost_en_cost_pct", "boost_cooldown_s", "boost_iframe_s", "glide_en_pct_per_s",
                    "glide_gravity_mult", "fly_climb_mps", "fly_move_mps", "boost_hold_mult", "boost_hold_en_pct_per_s", "boost_slide_s",
                    "fly_accel_mps2", "fly_drag_mps2", "fly_climb_accel_mps2", "fall_accel_mps2", "fall_terminal_mps"):
            row.pop(old, None)
        row.pop("overheat_recover_pct", None)
        print(row["id"], {k: v for k, v in new.items() if k not in ("ac6_sources",)})
    sheet["_about"] = (
        "One row per starting frame. class_row = CharaInitParam row used for this frame. Level must equal sum(stats) - 79. "
        "size_scale = the DLL scales the player model so the frame stands about 2x a Tarnished. MOVEMENT NUMBERS ARE REAL AC6 DATA: "
        "tools/ac6_numbers.py fills every ac6-derived column from the player's AC6 params (biped MovementAcTypeParam row, stock booster, "
        "a generator of the kit's maker family, the kit's own legs, TentativePlayerParam gravity), scaled by ac6_k (frame size / AC size) "
        "for lengths, speeds and accelerations; EN costs become a percent of the generator pool. Do not hand-edit those columns; edit the script. "
        "Quick Boost (tap dodge): qb_*; boost (hold dodge): ground_boost_* / air_boost_*; vertical boost (hold jump while airborne): vboost_*; "
        "airborne without thrust: air_h_*, fly_*_brake, gravity_mps2; landing: landing_brake_mps2; EN: en_*; hop_alt_m = the kit legs' jump altitude (not used yet). "
        "move_speed_scale = ground walk/run relative to the frame's size (AC6 stores only the leg walk/run anim rates 1.1/1.0/0.9; ours are tuned)."
    )
    json.dump(sheet, open(SHEET, "w", encoding="utf-8"), indent=2)


if __name__ == "__main__":
    main()
