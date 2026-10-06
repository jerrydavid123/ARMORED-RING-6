"""Build the mod's regulation.bin from the sheets.

Takes the vanilla Elden Ring params (unpacked by WitchyBND), edits the rows the sheets describe, repacks.
Sheets are the source of truth: change the sheet, then rerun this.

  python build_regulation.py <out regulation.bin>
"""
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SHEETS = os.path.join(HERE, "..", "sheets")
WITCHY = "C:/Users/ddean/modtools/witchybnd/WitchyBND.exe"
VANILLA = "C:/Users/ddean/modtools/work/vanilla/regulation-bin"
BUILD = "C:/Users/ddean/modtools/work/build/regulation-bin"

# SpEffect that makes the hit capsule as tall as the frame (the DLL scales the model; this scales what hits things)
CAPSULE_SPEFFECT = 99900100
# SpEffect with fallDamageRate 0, worn permanently by every frame (AC6 frames take no fall damage)
NOFALL_SPEFFECT = 99900101
CAPSULE_RATE = {r["id"]: r for r in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "sheets", "systems.json"), encoding="utf-8"))["rows"]}["sys_size"]["capsule_height_rate"]
WSTATS = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "sheets", "weapon_stats.json"), encoding="utf-8"))
SYSW = {r["id"]: r for r in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "sheets", "systems.json"), encoding="utf-8"))["rows"]}["sys_weapons"]
BULLET_BASE, ATK_BASE = 10400000, 40000  # Glintstone Pebble bullet and its attack row: a fast energy orb we retune
BULLET_ID0 = 99910000
FRAME_ARMOR_WEIGHT = 1.0  # pieces are nearly weightless: the frame never gets a fat roll


def read(path):
    return open(path, encoding="utf-8-sig").read()


def write(path, text):
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        f.write(text)


def row_span(text, rid):
    m = re.search(r'<row id="%d"[^>]*?/>' % rid, text)
    if not m:
        raise KeyError(f"row {rid} not found")
    return m.start(), m.end()


def set_attrs(text, rid, attrs):
    a, b = row_span(text, rid)
    row = text[a:b]
    for k, v in attrs.items():
        if re.search(r'\s%s="[^"]*"' % re.escape(k), row):
            row = re.sub(r'(\s%s=")[^"]*(")' % re.escape(k), lambda m: m.group(1) + str(v) + m.group(2), row)
        else:
            row = row[:-2].rstrip() + f' {k}="{v}" />'
    return text[:a] + row + text[b:]


def add_row(text, rid, attrs):
    """Insert a new row, keeping rows sorted by id."""
    ids = [int(m.group(1)) for m in re.finditer(r'<row id="(-?\d+)"', text)]
    nxt = next((i for i in ids if i > rid), None)
    attr_text = "".join(f' {k}="{v}"' for k, v in attrs.items())
    new = f'<row id="{rid}"{attr_text} />\n'
    if nxt is None:
        pos = text.index("</rows>")
    else:
        pos = text.index(f'<row id="{nxt}"')
    return text[:pos] + new + text[pos:]


def clone_row(text, base_id, new_id, attrs):
    """Copy an existing row under a new id (inserted in id order) and apply attrs."""
    a, b = row_span(text, base_id)
    row = text[a:b]
    row = re.sub(r'<row id="%d"' % base_id, '<row id="%d"' % new_id, row, count=1)
    ids = [int(m.group(1)) for m in re.finditer(r'<row id="(-?\d+)"', text)]
    nxt = next((i for i in ids if i > new_id), None)
    pos = text.index("</rows>") if nxt is None else text.index(f'<row id="{nxt}"')
    text = text[:pos] + row + "\n" + text[pos:]
    return set_attrs(text, new_id, attrs) if attrs else text


def frames():
    return json.load(open(os.path.join(SHEETS, "frames.json"), encoding="utf-8"))["rows"]


def main(out):
    if os.path.exists(BUILD):
        shutil.rmtree(BUILD)
    os.makedirs(os.path.dirname(BUILD), exist_ok=True)
    shutil.copytree(VANILLA, BUILD)

    edited = []

    # --- SpEffectParam: capsule height ---
    p = os.path.join(BUILD, "SpEffectParam.param.xml")
    t = read(p)
    t = add_row(t, CAPSULE_SPEFFECT, {"effectEndurance": "-1", "chrProxyHeightRate": str(CAPSULE_RATE), "effectTargetSelf": "1", "effectTargetPlayer": "1"})
    t = add_row(t, NOFALL_SPEFFECT, {"effectEndurance": "-1", "fallDamageRate": "0", "effectTargetSelf": "1", "effectTargetPlayer": "1"})
    write(p, t)
    edited.append("SpEffectParam")

    # --- EquipParamProtector: hide the Tarnished's own body, make the kit weightless, attach the capsule effect ---
    chara = read(os.path.join(BUILD, "CharaInitParam.param.xml"))
    p = os.path.join(BUILD, "EquipParamProtector.param.xml")
    t = read(p)
    hide = {f"invisibleFlag_SexVer{i:02d}": 1 for i in range(96)}
    for fr in frames():
        crow = int(fr["class_row"])
        m = re.search(r'<row id="%d"([^>]*?)/>' % crow, chara)
        a = dict(re.findall(r'(\w+)="([^"]*)"', m.group(1)))
        for slot in ("equip_Helm", "equip_Armer", "equip_Gaunt", "equip_Leg"):
            rid = int(a[slot])
            attrs = dict(hide)
            attrs["weight"] = FRAME_ARMOR_WEIGHT
            if slot == "equip_Armer":
                attrs["residentSpEffectId"] = NOFALL_SPEFFECT
                if CAPSULE_RATE != 1.0:
                    attrs["residentSpEffectId2"] = CAPSULE_SPEFFECT
            t = set_attrs(t, rid, attrs)
    write(p, t)
    edited.append("EquipParamProtector")

    # --- CharaInitParam: frame stats ---
    p = os.path.join(BUILD, "CharaInitParam.param.xml")
    t = read(p)
    for fr in frames():
        s = fr["stats"]
        t = set_attrs(t, int(fr["class_row"]), {
            "soulLv": fr["level"], "baseVit": s["vig"], "baseWil": s["mnd"], "baseEnd": s["end"], "baseStr": s["str"],
            "baseDex": s["dex"], "baseMag": s["int"], "baseFai": s["fai"], "baseLuc": s["arc"],
        })
    write(p, t)
    edited.append("CharaInitParam")

    # --- EquipParamWeapon: the six frame weapons and the energy cell, cloned from vanilla rows ---
    wrows = json.load(open(os.path.join(SHEETS, "weapons.json"), encoding="utf-8"))["rows"]
    wby = {r["id"]: r for r in wrows}
    p = os.path.join(BUILD, "EquipParamWeapon.param.xml")
    t = read(p)
    for w in wrows:
        if w["kind"] == "back":
            continue
        if w["kind"] == "ammo":
            t = clone_row(t, w["base_row"], w["param_row"], {"weight": 0})
        else:
            # requirements low so every frame can use every weapon without a penalty; weight light so rolls stay light
            t = clone_row(t, w["base_row"], w["param_row"], {
                "properStrength": w["str_req"], "properAgility": w["dex_req"], "properMagic": w["int_req"],
                "properFaith": 1, "properLuck": 1, "weight": w["weight"],
            })
    # melee weapons carry the real AC6 slash damage (energy blade = magic, hammer = physical), scaled to ER
    ds = SYSW["damage_scale"]
    for w in wrows:
        st = WSTATS.get(w["id"])
        if w["kind"] == "melee" and st:
            n = st["attacks"]["normal"]
            t = set_attrs(t, w["param_row"], {"attackBasePhysics": round(n["damage_phys"] * ds), "attackBaseMagic": round(n["damage_mag"] * ds),
                                              "correctStrength": 0, "correctAgility": 0, "correctMagic": 0, "correctFaith": 0, "correctLuck": 0})
    write(p, t)
    edited.append("EquipParamWeapon")

    # ---- gun projectiles: Bullet + AtkParam_Pc rows, AC6 speed/range/radius/damage; the DLL spawns them ----
    pb = os.path.join(BUILD, "Bullet.param.xml")
    pa = os.path.join(BUILD, "AtkParam_Pc.param.xml")
    tb, ta = read(pb), read(pa)
    gi = 0
    for w in wrows:
        st = WSTATS.get(w["id"])
        if w["kind"] not in ("ranged", "back") or not st:
            continue
        for k, label in enumerate(("normal", "charged")):
            a = st["attacks"].get(label) or st["attacks"]["normal"]
            rid = BULLET_ID0 + gi * 10 + k
            phys, mag = round(a["damage_phys"] * ds), round(a["damage_mag"] * ds)
            ta = clone_row(ta, ATK_BASE, rid, {"atkPhys": phys, "atkMag": mag, "atkPhysCorrection": 100, "atkMagCorrection": 100, "paramdexName": "AC6 " + w["id"] + " " + label})
            vmax = a.get("max_speed_mps", a["speed_mps"])
            life = round(a["range_m"] / max(vmax, 1.0) + 0.5, 2)
            energy = mag >= phys
            tb = clone_row(tb, BULLET_BASE, rid, {
                "atkId_Bullet": rid, "life": life, "dist": a["range_m"], "initVellocity": a["speed_mps"], "maxVellocity": vmax, "minVellocity": min(a["speed_mps"], vmax),
                "accelInRange": round((vmax - a["speed_mps"]) / 0.33, 1), "accelOutRange": round((vmax - a["speed_mps"]) / 0.33, 1), "accelTime": 0.33 if vmax > a["speed_mps"] else 0,
                "hitRadius": a["radius_m"], "homingAngle": 35 if a.get("homing_deg", 0) > 0 else 0, "isEnableAutoHoming": 1 if a.get("homing_deg", 0) > 0 else 0, "lockShootLimitAng": 15,
                "sfxId_Bullet": w.get("sfx_bullet", 523002 if energy else 5200001), "sfxId_Hit": w.get("sfx_hit", 523003 if energy else 5200002), "paramdexName": "AC6 " + w["id"] + " " + label})
        gi += 1
    # blade slash: a short, fat, fast energy bolt the DLL spawns in front of the mech at every lunge (AC6 blade damage)
    for w in wrows:
        st = WSTATS.get(w["id"])
        if w["kind"] == "melee" and st and "blade_bullet" not in globals().get("_done", set()):
            a = st["attacks"]["normal"]
            rid = BULLET_ID0 + 100
            phys, mag = round(a["damage_phys"] * ds), round(a["damage_mag"] * ds)
            ta = clone_row(ta, ATK_BASE, rid, {"atkPhys": phys, "atkMag": mag, "atkPhysCorrection": 100, "atkMagCorrection": 100, "paramdexName": "AC6 " + w["id"] + " slash"})
            tb = clone_row(tb, BULLET_BASE, rid, {
                "atkId_Bullet": rid, "life": 0.14, "dist": 8, "initVellocity": 60, "maxVellocity": 60, "minVellocity": 60,
                "accelInRange": 0, "accelOutRange": 0, "accelTime": 0, "hitRadius": 2.2, "homingAngle": 0, "isEnableAutoHoming": 0,
                "sfxId_Bullet": w.get("sfx_slash", 450872), "sfxId_Hit": w.get("sfx_slash_hit", 450874), "paramdexName": "AC6 " + w["id"] + " slash"})
            globals().setdefault("_done", set()).add("blade_bullet")
    # look-development rows: the same projectile with different stock effects, fired in turn by the DLL's 'dbg' self test
    for i, sfx in enumerate(SYSW.get("debug_sfx", [])):
        rid = 99919000 + i
        tb = clone_row(tb, BULLET_BASE, rid, {"atkId_Bullet": BULLET_ID0, "life": 1.6, "dist": 120, "initVellocity": 40, "maxVellocity": 40, "minVellocity": 40,
                                              "accelInRange": 0, "accelOutRange": 0, "accelTime": 0, "hitRadius": 0.3, "homingAngle": 0, "isEnableAutoHoming": 0,
                                              "sfxId_Bullet": sfx, "paramdexName": "AC6 debug sfx %d" % sfx})
    write(pb, tb)
    write(pa, ta)
    edited += ["Bullet", "AtkParam_Pc"]

    # --- CharaInitParam: each frame starts holding its own weapons and carrying energy cells ---
    p = os.path.join(BUILD, "CharaInitParam.param.xml")
    t = read(p)
    ammo = next(w for w in wrows if w["kind"] == "ammo")
    for fr in frames():
        t = set_attrs(t, int(fr["class_row"]), {
            "equip_Wep_Right": wby[fr["start_weapon_right"]]["param_row"],
            "equip_Wep_Left": wby[fr["start_weapon_left"]]["param_row"],
            "equip_Subwep_Right": -1, "equip_Subwep_Left": -1, "equip_Subwep_Right3": -1, "equip_Subwep_Left3": -1,
            "equip_Bolt": -1, "boltNum": 0,
            # the tutorial skip bypasses the game's own flask hand-out, so the frame starts with its repair flask:
            # goods 1001 = Flask of Crimson Tears (charged state), 4 charges
            "item_01": 1001, "itemNum_01": 4,
        })
    write(p, t)

    # --- LockCamParam: pull the follow camera back for the bigger frame ---
    systems = {r["id"]: r for r in json.load(open(os.path.join(SHEETS, "systems.json"), encoding="utf-8"))["rows"]}
    cam = systems["sys_camera"]
    p = os.path.join(BUILD, "LockCamParam.param.xml")
    t = read(p)
    for rid in cam["rows"]:
        m = re.search(r'<row id="%d"([^>]*?)/>' % rid, t)
        a = dict(re.findall(r'(\w+)="([^"]*)"', m.group(1)))
        def num(k, default):
            return float(a.get(k, default))
        new_attrs = {
            "camDistTarget": round(num("camDistTarget", 3.8) * cam["dist_scale"], 3),
            "chrOrgOffset_Y": round(num("chrOrgOffset_Y", 1.45) * cam["height_scale"], 3),
            "chrLockRangeMaxRadius": round(num("chrLockRangeMaxRadius", 15.0) * cam["lock_range_scale"], 3),
        }
        t = set_attrs(t, rid, new_attrs)
        print("camera row", rid, "->", new_attrs)
    write(p, t)
    edited.append("LockCamParam")

    # --- repack each edited param, then the whole regulation ---
    for name in edited:
        xml = os.path.join(BUILD, name + ".param.xml")
        r = subprocess.run([WITCHY, "--passive", xml], capture_output=True, text=True, timeout=600)
        print(name, "->", (r.stdout + r.stderr).strip().splitlines()[-1:] or "ok")
        # WitchyBND writes <name>.param next to the xml; drop the xml so the folder repack sees only params
        os.remove(xml)
    # pack the container with our own tool (WitchyBND's folder repack is not usable headless)
    ER = "C:/program files (x86)/steam/steamapps/common/ELDEN RING/Game"
    tool = "C:/Users/ddean/modtools/work/ac6convert-bin/Ac6Convert.exe"
    env = dict(os.environ, DOTNET_ROOT="C:/Users/ddean/modtools/dotnet")
    r = subprocess.run([tool, "regpack", ER, ER + "/regulation.bin", BUILD, out] + edited, capture_output=True, text=True, env=env, timeout=900)
    print((r.stdout + r.stderr).strip())
    if r.returncode != 0:
        raise SystemExit("regpack failed")
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main(sys.argv[1])
