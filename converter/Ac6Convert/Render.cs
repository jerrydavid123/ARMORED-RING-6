using System.Drawing;
using System.Numerics;
using SoulsFormats;

namespace Ac6Convert;

/// <summary>
/// `render &lt;oodle dir&gt; &lt;out.png&gt; &lt;spec&gt;...` : flat-shaded orthographic views (front / side / back) of FLVER parts, to check how
/// parts sit together. Spec: file.partsbnd.dcx|color|[attachFile@nodeName]|[scale]|[yaw]
///   file       the part (first .flver inside)
///   color      r,g,b
///   attach     optional: place the part at a node of another part (its world matrix, row-vector convention); `@` separates
///              the file and the node name; the node's own part is not drawn unless listed itself
///   scale      optional uniform scale about the origin applied last
///   yaw        optional degrees about Y applied to the weapon before the attach matrix (to test mount orientation)
/// </summary>
internal static class Render
{
    private sealed class Tri { public Vector3 A, B, C; public Vector3 N; public Color Col; public bool Frame; }

    private static FLVER2 Load(string path) =>
        FLVER2.Read(BND4.Read(path).Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);

    private static Matrix4x4[] World(FLVER2 f)
    {
        var w = new Matrix4x4[f.Nodes.Count];
        for (int i = 0; i < f.Nodes.Count; i++)
        {
            var loc = f.Nodes[i].ComputeLocalTransform();
            w[i] = f.Nodes[i].ParentIndex >= 0 ? loc * w[f.Nodes[i].ParentIndex] : loc;
        }
        return w;
    }

    public static int Run(string outPng, string[] specs)
    {
        var tris = new List<Tri>();
        foreach (var spec in specs)
        {
            var p = spec.Split('|');
            var fl = Load(p[0]);
            var c = p[1].Split(',').Select(int.Parse).ToArray();
            var col = Color.FromArgb(c[0], c[1], c[2]);
            bool frame = !(p.Length > 2 && p[2].Length > 0);
            Matrix4x4 m = Matrix4x4.Identity;
            if (p.Length > 4 && p[4].Length > 0) m = Matrix4x4.CreateRotationY(float.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180f);
            if (p.Length > 2 && p[2].Length > 0)
            {
                var at = p[2].Split('@');
                var host = Load(at[0]);
                int k = host.Nodes.FindIndex(n => n.Name == at[1]);
                if (k < 0) throw new Exception($"node {at[1]} not found in {at[0]}");
                m = m * World(host)[k];
            }
            float scale = p.Length > 3 && p[3].Length > 0 ? float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
            foreach (var mesh in fl.Meshes)
            {
                if (mesh.Vertices.Count < 20) continue;
                var pos = mesh.Vertices.Select(v => Vector3.Transform(v.Position, m) * scale).ToList();
                var fs = mesh.FaceSets.First();
                var idx = fs.Triangulate(true);
                for (int i = 0; i + 2 < idx.Count; i += 3)
                {
                    int a = idx[i], b = idx[i + 1], cc = idx[i + 2];
                    if (a >= pos.Count || b >= pos.Count || cc >= pos.Count || a == b || b == cc || a == cc) continue;
                    var n = Vector3.Cross(pos[b] - pos[a], pos[cc] - pos[a]);
                    if (n.LengthSquared() < 1e-12f) continue;
                    tris.Add(new Tri { A = pos[a], B = pos[b], C = pos[cc], N = Vector3.Normalize(n), Col = col, Frame = frame });
                }
            }
        }
        var fr = tris.Where(t => t.Frame).ToList();
        var min = fr.Aggregate(new Vector3(float.MaxValue), (a, t) => Vector3.Min(a, Vector3.Min(t.A, Vector3.Min(t.B, t.C))));
        var max = fr.Aggregate(new Vector3(float.MinValue), (a, t) => Vector3.Max(a, Vector3.Max(t.A, Vector3.Max(t.B, t.C))));
        Console.WriteLine($"{tris.Count} triangles, bbox {min} .. {max}");

        // views: (name, screen-x axis, depth axis (toward viewer positive))
        // AC6 faces -Z, its left is +X. Front view: viewer at -Z looking +Z: screen right = -X ... keep it simple and label.
        var views = new (string name, Func<Vector3, (float x, float y, float d)> proj)[]
        {
            ("front (viewer in front of the AC, AC's left on image right)", v => (v.X, v.Y, -v.Z)),
            ("side (AC faces right)", v => (-v.Z, v.Y, v.X)),
            ("back", v => (-v.X, v.Y, v.Z)),
        };
        int W = 520, H = 720;
        using var bmp = new Bitmap(W * views.Length, H);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        float spanY = max.Y - min.Y;
        float spanX = Math.Max(max.X - min.X, max.Z - min.Z) * 1.9f;
        float sc = Math.Min((H - 60) / spanY, (W - 40) / spanX);
        var light = Vector3.Normalize(new Vector3(0.3f, 0.6f, -0.8f));
        for (int vi = 0; vi < views.Length; vi++)
        {
            var depth = new float[W * H];
            Array.Fill(depth, float.MinValue);
            var px = new int[W * H];
            Array.Fill(px, Color.White.ToArgb());
            float cx = vi == 1 ? (-(min.Z + max.Z) / 2) : (vi == 0 ? (min.X + max.X) / 2 : -(min.X + max.X) / 2);
            foreach (var t in tris)
            {
                var a = views[vi].proj(t.A); var b = views[vi].proj(t.B); var c = views[vi].proj(t.C);
                float Sx(float x) => W / 2f + (x - cx) * sc;
                float Sy(float y) => H - 30 - (y - min.Y) * sc;
                var pa = new PointF(Sx(a.x), Sy(a.y)); var pb = new PointF(Sx(b.x), Sy(b.y)); var pc = new PointF(Sx(c.x), Sy(c.y));
                float shade = 0.45f + 0.55f * Math.Abs(Vector3.Dot(t.N, light));
                var col = Color.FromArgb((int)(t.Col.R * shade), (int)(t.Col.G * shade), (int)(t.Col.B * shade)).ToArgb();
                int x0 = (int)Math.Max(0, Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X)))), x1 = (int)Math.Min(W - 1, Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X))));
                int y0 = (int)Math.Max(0, Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)))), y1 = (int)Math.Min(H - 1, Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y))));
                float den = (pb.Y - pc.Y) * (pa.X - pc.X) + (pc.X - pb.X) * (pa.Y - pc.Y);
                if (Math.Abs(den) < 1e-6f) continue;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float l1 = ((pb.Y - pc.Y) * (x - pc.X) + (pc.X - pb.X) * (y - pc.Y)) / den;
                        float l2 = ((pc.Y - pa.Y) * (x - pc.X) + (pa.X - pc.X) * (y - pc.Y)) / den;
                        float l3 = 1 - l1 - l2;
                        if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                        float d = l1 * a.d + l2 * b.d + l3 * c.d;
                        if (d > depth[y * W + x]) { depth[y * W + x] = d; px[y * W + x] = col; }
                    }
            }
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) bmp.SetPixel(vi * W + x, y, Color.FromArgb(px[y * W + x]));
            g.DrawString(views[vi].name, SystemFonts.DefaultFont, Brushes.Black, vi * W + 4, 4);
        }
        bmp.Save(outPng);
        Console.WriteLine($"wrote {outPng}");
        return 0;
    }
}
