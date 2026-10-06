"""Port every Armored Core VI player animation that has a counterpart (same a000_<id>) in Elden Ring's c0000 sets.

Pipeline per clip: HkxTool port  (AC6 clip -> retargeted onto the ER skeleton -> compressed -> patched into the ER clip's file)
Then each Elden Ring animation archive (c0000_a00_hi/md/lo/... .anibnd.dcx) is rebuilt with the ported clips replacing the
originals, ready for labmod/chr.

Two kinds of port:
  * identity: the same a000_<id> exists in both games (idle, walk, run, sprint, ...): AC6 clip -> ER clip of that id
  * mapped:   tools/anim_map.json says which AC6 clip(s) replace which ER ids (jump, fall, landing, ...)
Every ported clip is time-fitted to the ER clip it replaces (ER keeps authority over timing, root motion and TAE events).

Needs: the Ac6Convert and HkxTool builds (work/ac6convert-bin, work/hkxtool-bin), both games installed.
  python batch_port_anims.py [--only a000_000000,a000_020100]
"""
import glob
import json
import os
import re
import subprocess
import sys

W = "C:/Users/ddean/modtools/work"
CMP = f"{W}/cmp"
AC = f"{W}/ac6convert-bin/Ac6Convert.exe"
HK = f"{W}/hkxtool-bin/HkxTool.exe"
ER_GAME = "C:/program files (x86)/steam/steamapps/common/ELDEN RING/Game"
AC6_GAME = "D:/SteamLibrary/steamapps/common/ARMORED CORE VI FIRES OF RUBICON/Game"
ENV = dict(os.environ, DOTNET_ROOT="C:/Users/ddean/modtools/dotnet")

AC6_ARCHIVES = ["c0000_a000_00", "c0000_a000_01", "c0000_a000_02", "c0000_a000_03", "c0000_a100_01", "c0000_a100_02", "c0000_a211_02"]
ER_ARCHIVES = ["c0000_a00_hi", "c0000_a00_md", "c0000_a00_lo", "c0000_a0x", "c0000_a1x", "c0000_a2x"]


def run(cmd, check=True):
    r = subprocess.run(cmd, capture_output=True, text=True, env=ENV)
    if check and r.returncode != 0:
        raise RuntimeError(f"{cmd[:3]} failed: {(r.stdout + r.stderr)[-600:]}")
    return r


def ensure_extracted(game, archive, folder):
    out = f"{CMP}/{folder}/all_{archive}"
    if not glob.glob(out + "/*.hkx"):
        run([AC, "unbnd", ER_GAME, f"{CMP}/{folder}/chr/{archive}.anibnd.dcx", out])
    return out


def index(folder):
    """id -> (hkx path, compendium path)"""
    res = {}
    comp = glob.glob(folder + "/*.compendium")
    comp = comp[0] if comp else None
    for f in glob.glob(folder + "/*.hkx"):
        m = re.search(r"_(a\d{3}_\d{6})\.hkx$", f.replace("\\", "/"))
        if m:
            res[m.group(1)] = (f.replace("\\", "/"), comp.replace("\\", "/") if comp else None)
    return res


def load_map():
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "anim_map.json")
    return json.load(open(p, encoding="utf-8"))["mappings"]


def main():
    only = None
    if "--only" in sys.argv:
        only = set(sys.argv[sys.argv.index("--only") + 1].split(","))
    # make sure the archives are on disk
    for a in AC6_ARCHIVES:
        if not os.path.exists(f"{CMP}/ac6/chr/{a}.anibnd.dcx"):
            print("missing AC6 archive", a); return 1
    ac_idx, er_idx, er_arch = {}, {}, {}
    for a in AC6_ARCHIVES:
        for k, v in index(ensure_extracted("ac6", a, "ac6")).items():
            ac_idx[k] = v
    for a in ER_ARCHIVES:
        for k, v in index(ensure_extracted("er", a, "er")).items():
            er_idx[k] = v
            er_arch[k] = a
    mapped = {}  # er id -> (list of ac6 ids)
    nofit = set()
    zeroroot = set()
    for m in load_map():
        for e in m["er"]:
            e = e.replace("a000_", "a000_")
            if e not in er_idx:
                print("map: ER clip missing", e); continue
            if any(x not in ac_idx for x in m["ac6"]):
                print("map: AC6 clip missing in", m["name"]); continue
            mapped[e] = m["ac6"]
            if m.get("fit", True) is False:
                nofit.add(e)
            if m.get("zero_root", False):
                zeroroot.add(e)
    # same-id ports only for the body set (a000_); weapon sets (a2xx...) are ported through anim_map.json
    jobs = {cid: [cid] for cid in sorted(set(ac_idx) & set(er_idx)) if cid.startswith("a000_")}
    jobs.update(mapped)
    if only:
        jobs = {k: v for k, v in jobs.items() if k in only}
    print(f"{len(jobs)} clips to port ({len(mapped)} mapped)")
    ac_skel = f"{CMP}/ac6/anibnd/4000000_Skeleton.hkx"
    er_skel = f"{CMP}/er/anibnd/4000000_Skeleton.hkx"
    by_arch = {}
    failed = []
    for cid, sources in jobs.items():
        out_dir = f"{CMP}/out/{er_arch[cid]}"
        os.makedirs(out_dir, exist_ok=True)
        out = f"{out_dir}/{cid}.hkx"
        if os.path.exists(out):
            os.remove(out)
        src = ";".join(ac_idx[x][0] for x in sources)
        r = run([HK, "port", src, ac_idx[sources[0]][1], ac_skel, er_skel, er_idx[cid][0], er_idx[cid][1], out, f"{W}/hkxtool-bin", "0.1459"] + ([] if cid in nofit else ["--fit"]) + (["--noroot"] if cid in zeroroot else []), check=False)
        if r.returncode == 3:
            print("skipped (additive)", cid)
            continue
        if r.returncode != 0 or not os.path.exists(out):
            failed.append(cid)
            print("FAILED", cid, (r.stdout + r.stderr)[-300:].replace(chr(10), " | "))
            continue
        by_arch.setdefault(er_arch[cid], []).append((cid, out))
        print("ported", cid, "<-", "+".join(sources), "->", er_arch[cid])
    os.makedirs(f"{W}/stage_anim/chr", exist_ok=True)
    for arch, items in by_arch.items():
        pairs = [f"{cid}.hkx={path}" for cid, path in items]
        run([AC, "replacebnd", ER_GAME, f"{CMP}/er/chr/{arch}.anibnd.dcx", f"{W}/stage_anim/chr/{arch}.anibnd.dcx"] + pairs)
        print("rebuilt", arch, len(items), "clips")
    print("failed:", failed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
