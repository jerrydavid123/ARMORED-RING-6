using System.Drawing;
using System.Numerics;
using SoulsAssetPipeline.Animation;

// HkxTool sheet <out.png> <compendium> <ac6 skeleton.hkx> <clip1.hkx> [clip2.hkx ...]
// Side-view stick figures (forward is -Z in the AC6 skeleton, drawn to the right), 5 frames per clip, one row per clip.
internal static class ClipSheet
{
    static readonly string[] Skip = { "Twist", "Finger", "EX", "Link", "Target", "Weapon", "Rot_XYZ", "Master", "BD_Root", "Root_" };

    public static int Run(string[] a)
    {
        string outPng = a[1], comp = a[2], skelPath = a[3];
        var clips = a.Skip(4).ToArray();
        var skel = AnimPort.LoadSkel(skelPath);
        var fakeSkel = HKX.GenFakeFromTagFile(File.ReadAllBytes(skelPath), null);
        HKX.HKASkeleton hkSkel = null;
        foreach (var o in fakeSkel.DataSection.Objects) if (o is HKX.HKASkeleton s) hkSkel = s;
        bool draw(int b)
        {
            string n = skel.Names[b];
            foreach (var k in Skip) if (n.Contains(k)) return false;
            return true;
        }
        const int cellW = 150, cellH = 190, frames = 5, label = 120;
        using var bmp = new Bitmap(label + cellW * frames, cellH * clips.Length);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var font = new Font("Arial", 9);
        using var pen = new Pen(Color.Black, 2);
        using var penL = new Pen(Color.Blue, 2);
        using var penR = new Pen(Color.Red, 2);
        int row = 0;
        foreach (var clip in clips)
        {
            string id = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(clip), @"a\d{3}_\d{6}").Value;
            var fake = HKX.GenFakeFromTagFile(File.ReadAllBytes(clip), File.ReadAllBytes(comp));
            HKX.HKAAnimationBinding bind = null; HKX.HKASplineCompressedAnimation anim = null; HKX.HKADefaultAnimatedReferenceFrame rf = null;
            foreach (var o in fake.DataSection.Objects)
            {
                if (o is HKX.HKAAnimationBinding b) bind = b;
                else if (o is HKX.HKASplineCompressedAnimation sa) anim = sa;
                else if (o is HKX.HKADefaultAnimatedReferenceFrame r) rf = r;
            }
            var data = new HavokAnimationData_SplineCompressed(0, id, hkSkel, rf, bind, anim);
            g.DrawString($"{id}\n{anim.Duration:0.00}s", font, Brushes.Black, 2, row * cellH + 4);
            for (int c = 0; c < frames; c++)
            {
                int fr = (int)Math.Round(c * (anim.FrameCount - 1) / (double)(frames - 1));
                var lq = new Quaternion[skel.Count]; var lt = new Vector3[skel.Count];
                for (int b = 0; b < skel.Count; b++) { var tr = data.GetTransformOnFrameByBone(b, fr, false); lq[b] = Quaternion.Normalize(tr.Rotation); lt[b] = tr.Translation; }
                AnimPort.Fk(skel, lq, lt, out _, out var wp);
                float sc = 14f;
                float ox = label + c * cellW + cellW / 2f, oy = row * cellH + cellH - 12;
                // pelvis-relative horizontally so the figure stays in its cell; the ground line is y = 0
                float cx = wp[10].Z;
                PointF P(Vector3 v) => new PointF(ox - (v.Z - cx) * sc, oy - v.Y * sc);
                g.DrawLine(Pens.LightGray, label + c * cellW, oy, label + (c + 1) * cellW, oy);
                for (int b = 0; b < skel.Count; b++)
                {
                    int p = skel.Parent[b];
                    if (p < 0 || !draw(b) || !draw(p)) continue;
                    var pen2 = skel.Names[b].StartsWith("L_") ? penL : skel.Names[b].StartsWith("R_") ? penR : pen;
                    g.DrawLine(pen2, P(wp[p]), P(wp[b]));
                }
                g.DrawString($"f{fr}", font, Brushes.Gray, label + c * cellW + 2, row * cellH + 2);
            }
            row++;
        }
        bmp.Save(outPng);
        Console.WriteLine($"wrote {outPng}");
        return 0;
    }
}
