using System.Globalization;
using System.Numerics;
using SoulsAssetPipeline.Animation;

// HkxTool stats <clips dir> <compendium> <ac6 skeleton.hkx>
// One line per clip: id, frames, duration, root-motion travel (x,y,z,yaw) in the clip's own units, and the pelvis height range.
internal static class ClipStats
{
    public static int Run(string[] a)
    {
        string dir = a[1], comp = a[2], skelPath = a[3];
        var ci = CultureInfo.InvariantCulture;
        var skel = AnimPort.LoadSkel(skelPath);
        var fakeSkel = HKX.GenFakeFromTagFile(File.ReadAllBytes(skelPath), null);
        HKX.HKASkeleton hkSkel = null;
        foreach (var o in fakeSkel.DataSection.Objects) if (o is HKX.HKASkeleton s) hkSkel = s;
        int pelvis = -1;
        for (int i = 0; i < skel.Count; i++) if (skel.Names[i] == "Pelvis") pelvis = i;
        foreach (var f in Directory.GetFiles(dir, "*.hkx").OrderBy(x => x))
        {
            string id = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"a\d{3}_\d{6}").Value;
            if (id.Length == 0) continue;
            string basic = "";
            try
            {
                var fake = HKX.GenFakeFromTagFile(File.ReadAllBytes(f), File.ReadAllBytes(comp));
                HKX.HKAAnimationBinding bind = null; HKX.HKASplineCompressedAnimation anim = null; HKX.HKADefaultAnimatedReferenceFrame rf = null;
                foreach (var o in fake.DataSection.Objects)
                {
                    if (o is HKX.HKAAnimationBinding b) bind = b;
                    else if (o is HKX.HKASplineCompressedAnimation sa) anim = sa;
                    else if (o is HKX.HKADefaultAnimatedReferenceFrame r) rf = r;
                }
                basic = $"frames={anim.FrameCount} dur={anim.Duration.ToString("0.00", ci)}";
                var data = new HavokAnimationData_SplineCompressed(0, id, hkSkel, rf, bind, anim);
                string motion = "none";
                if (rf != null && rf.ReferenceFrameSamples.Size > 0)
                {
                    var first = rf.ReferenceFrameSamples[0].Vector; var last = rf.ReferenceFrameSamples[(int)rf.ReferenceFrameSamples.Size - 1].Vector;
                    motion = $"{last.X - first.X:0.00},{last.Y - first.Y:0.00},{last.Z - first.Z:0.00},{(last.W - first.W) * 57.2958f:0}deg";
                }
                int Find(string n) { for (int i = 0; i < skel.Count; i++) if (skel.Names[i] == n) return i; return -1; }
                int iRoot = Find("Root"), iPel = Find("Pelvis"), iLt = Find("L_Toe"), iRt = Find("R_Toe"), iHead = Find("Head");
                var pelY = new List<float>(); var toeLo = new List<float>(); var toeHi = new List<float>(); var rootP = new List<Vector3>();
                Vector3 leanSum = Vector3.Zero;
                for (int fr = 0; fr < anim.FrameCount; fr++)
                {
                    var lq = new Quaternion[skel.Count]; var lt = new Vector3[skel.Count];
                    for (int b = 0; b < skel.Count; b++) { var tr = data.GetTransformOnFrameByBone(b, fr, false); lq[b] = Quaternion.Normalize(tr.Rotation); lt[b] = tr.Translation; }
                    AnimPort.Fk(skel, lq, lt, out _, out var wp);
                    rootP.Add(wp[iRoot]); pelY.Add(wp[iPel].Y);
                    float ty1 = wp[iLt].Y - wp[iPel].Y, ty2 = wp[iRt].Y - wp[iPel].Y;
                    toeLo.Add(Math.Min(ty1, ty2)); toeHi.Add(Math.Max(ty1, ty2));
                    leanSum += wp[iHead] - wp[iPel];
                }
                var rd = rootP[rootP.Count - 1] - rootP[0]; leanSum /= anim.FrameCount;
                string F(float v) => v.ToString("0.00", ci);
                motion += $" rootD=({F(rd.X)},{F(rd.Y)},{F(rd.Z)}) pelY={F(pelY.Min())}..{F(pelY.Max())} toeRelPel={F(toeLo.Min())}..{F(toeHi.Max())} headRelPel=({F(leanSum.X)},{F(leanSum.Y)},{F(leanSum.Z)})";
                float lo = 0, hi = 0;
                Console.WriteLine($"{id} frames={anim.FrameCount} dur={anim.Duration.ToString("0.00", ci)} motion[x,y,z,yaw]={motion} pelvisY={lo.ToString("0.00", ci)}..{hi.ToString("0.00", ci)}");
            }
            catch (Exception e) { Console.WriteLine($"{id} {basic} ERROR {e.Message}"); }
        }
        return 0;
    }
}
