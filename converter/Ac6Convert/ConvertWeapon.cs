using System.Numerics;
using System.Text.Json;
using SoulsFormats;

namespace Ac6Convert;

/// <summary>
/// Turns an AC6 weapon model into an Elden Ring weapon model that replaces an existing ER weapon file.
/// The ER partsbnd is the template (skeleton, dummy points, material, vertex layout, file list); the geometry is the
/// AC6 weapon, rotated so its barrel/blade runs down -Y like Elden Ring weapons do, and scaled to a target length.
/// </summary>
internal static class ConvertWeapon
{
    private static readonly Dictionary<string, (byte[] body, byte[] glow)> Palettes = new()
    {
        ["cyan"] = (new byte[] { 96, 102, 110 }, new byte[] { 90, 225, 255 }),
        ["orange"] = (new byte[] { 96, 102, 110 }, new byte[] { 255, 150, 50 }),
    };

    // top rows of the atlas are reserved for the glowing parts
    private const float GlowBand = 0.06f;

    // AC6 weapons point their business end down -Z (the AC faces -Z); Elden Ring weapons point down -Y. Rotate about X.
    private static Vector3 ToEr(Vector3 p, float rollDeg)
    {
        var q = new Vector3(p.X, p.Z, -p.Y);
        if (rollDeg == 0) return q;
        float a = rollDeg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector3(q.X * c + q.Z * s, q.Y, -q.X * s + q.Z * c);
    }

    public static int Run(string sheetPath, string erParts, string acParts, string outParts, string tmp, string palette)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(sheetPath));
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() is "ammo" or "back") continue;
            string id = row.GetProperty("id").GetString()!;
            Console.WriteLine($"== {id}");
            float hilt = row.TryGetProperty("hilt_len_m", out var hp) ? hp.GetSingle() : 0.32f;
            float bladeW = row.TryGetProperty("blade_half_w_m", out var bp) ? bp.GetSingle() : 0.04f;
            Build(erParts, acParts, outParts, tmp,
                row.GetProperty("er_model").GetString()!, row.GetProperty("ac6_file").GetString()!, row.GetProperty("ac6_tex").GetString()!,
                row.GetProperty("tip_len_m").GetSingle(), row.GetProperty("roll_deg").GetSingle(), row.GetProperty("glow").GetString()!, hilt, bladeW);
        }
        return 0;
    }

    private static string KindOf(string name)
    {
        string n = name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? name[..^2] : name;
        return n[^1].ToString().ToLowerInvariant();
    }

    private static (string dxgi, bool srgb) FormatFor(byte fmt, char kind) => (fmt, kind) switch
    {
        (0, 'a') => ("BC1_UNORM", false),
        (1, 'a') => ("BC1_UNORM_SRGB", true),
        (102, 'a') => ("BC7_UNORM_SRGB", true),
        (_, 'm') => ("BC4_UNORM", false),
        (_, 'a') => ("BC7_UNORM", false),
        _ => ("BC7_UNORM", false),
    };

    private static void Build(string erParts, string acParts, string outParts, string tmp, string erModel, string acFile, string acTex, float tipLen, float roll, string glowName, float hiltLen, float bladeHalfW)
    {
        string erBase = $"wp_a_{erModel}";
        var tplBnd = BND4.Read(Path.Combine(erParts, erBase + ".partsbnd.dcx"));
        var lowBnd = BND4.Read(Path.Combine(erParts, erBase + "_l.partsbnd.dcx"));
        var tplMain = tplBnd.Files.First(f => f.ID == 200);
        var tpl = FLVER2.Read(tplMain.Bytes);

        var ac = FLVER2.Read(BND4.Read(Path.Combine(acParts, acFile + ".partsbnd.dcx")).Files.First(f => f.ID == 200).Bytes);
        string texStem = acTex.Contains('_') && acTex.Split('_').Length > 2 ? string.Join("_", acTex.Split('_').Take(2)) : acTex;
        var acTpf = TPF.Read(Path.Combine(acParts, texStem.ToLowerInvariant() + "_l.tpf.dcx"));
        var acWorld = Skel.WorldMatrices(ac.Nodes);

        // meshes that become the weapon: the body and the laser blade, not the small emissive/eye bits
        bool Wanted(FLVER2.Mesh m)
        {
            string mtd = ac.Materials[m.MaterialIndex].MTD;
            if (mtd.Contains("Eyecolor", StringComparison.OrdinalIgnoreCase) || mtd.Contains("Emissive", StringComparison.OrdinalIgnoreCase)
                || mtd.Contains("Scroll", StringComparison.OrdinalIgnoreCase) || mtd.Contains("Plasmawire", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        bool IsGlow(FLVER2.Mesh m) => ac.Materials[m.MaterialIndex].MTD.Contains("Laser", StringComparison.OrdinalIgnoreCase);
        var meshes = ac.Meshes.Where(Wanted).ToList();
        // laser blades: AC6's blade is a fan of animated ribbons ("Laser" material, tens of metres long), which does not
        // survive a static ER model. Keep the hilt/body, size the weapon from it, and generate a clean energy blade.
        var bodyMeshes = meshes.Where(m => !IsGlow(m)).ToList();
        bool blade = bodyMeshes.Count > 0 && bodyMeshes.Count < meshes.Count;
        if (blade) meshes = bodyMeshes;

        Vector3 Pos(FLVER2.Mesh m, FLVER.Vertex v)
        {
            var p = v.Position;
            if (!m.UseBoneWeights && m.NodeIndex >= 0) p = Vector3.Transform(p, acWorld[m.NodeIndex]);
            return ToEr(p, roll);
        }

        // scale so the tip (most negative Y) sits at tipLen from the grip
        float minY = meshes.SelectMany(m => m.Vertices.Select(v => Pos(m, v).Y)).Min();
        float maxY = meshes.SelectMany(m => m.Vertices.Select(v => Pos(m, v).Y)).Max();
        float s = blade ? hiltLen / Math.Max(0.01f, maxY - minY) : tipLen / Math.Max(0.01f, -minY);
        float bladeStart = minY * s + 0.02f;           // negative: just inside the front of the hilt
        if (blade && -bladeStart + 0.3f > tipLen) tipLen = -bladeStart + 0.3f;
        Console.WriteLine($"  AC6 {acFile}: {meshes.Count} meshes{(blade ? " + generated energy blade" : "")}, tip at {minY:F2}, rear at {maxY:F2} -> scale {s:F4}, tip {tipLen} m");

        int metalMat = 0;
        var tm = tpl.Materials[metalMat];
        var tplMesh = tpl.Meshes.First(m => m.MaterialIndex == metalMat);
        var layout = tpl.BufferLayouts[tplMesh.VertexBuffers[0].LayoutIndex];
        int uvCap = 0, tanCap = 0, colCap = 0;
        foreach (var mem in layout)
        {
            string sem = mem.Semantic.ToString();
            if (sem == "UV") uvCap += (mem.Type.ToString().Contains('4') ? 2 : 1);
            else if (sem == "Tangent") tanCap++;
            else if (sem == "VertexColor") colCap++;
        }

        var flver = new FLVER2
        {
            Header = new FLVER2.FLVERHeader
            {
                BigEndian = tpl.Header.BigEndian, Version = tpl.Header.Version, Unicode = tpl.Header.Unicode,
                Unk4A = tpl.Header.Unk4A, Unk4B = tpl.Header.Unk4B, Unk4C = tpl.Header.Unk4C, Unk5C = tpl.Header.Unk5C,
                Unk5D = tpl.Header.Unk5D, Unk68 = tpl.Header.Unk68, SpecialModifier = tpl.Header.SpecialModifier, Unk74 = tpl.Header.Unk74,
            },
            Dummies = new List<FLVER.Dummy>(),
            GXLists = tpl.GXLists, Nodes = tpl.Nodes, Skeletons = tpl.Skeletons,
            BufferLayouts = new List<FLVER2.BufferLayout> { layout },
        };
        // dummy points (hit boxes, effects, muzzle) keep their places along the weapon, stretched to the new length
        float oldTip = Math.Max(0.05f, -tpl.Header.BoundingBoxMin.Y);
        float r = tipLen / oldTip;
        foreach (var d in tpl.Dummies)
        {
            var nd = new FLVER.Dummy(d);
            nd.Position = new Vector3(d.Position.X, d.Position.Y * r, d.Position.Z);
            flver.Dummies.Add(nd);
        }
        var mat = new FLVER2.Material { Name = tm.Name, MTD = tm.MTD, GXIndex = tm.GXIndex };
        foreach (var t in tm.Textures) mat.Textures.Add(new FLVER2.Texture(t.ParamName, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C));
        flver.Materials.Add(mat);

        var bbMin = new Vector3(float.MaxValue); var bbMax = new Vector3(float.MinValue);
        foreach (var sm in meshes)
        {
            bool glow = IsGlow(sm);
            var mesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = tplMesh.NodeIndex, UseBoneWeights = false, Dynamic = 0 };
            mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in sm.Vertices)
            {
                var p = Pos(sm, v) * s;
                var n = ToEr(v.Normal, roll);
                var nv = new FLVER.Vertex(uvCap, tanCap, colCap);
                nv.Positions.Add(p);
                nv.Normals.Add(n);
                nv.NormalWs.Add(0);
                var tg = v.Tangents.Count > 0 ? v.Tangents[0] : new Vector4(1, 0, 0, 1);
                var tt = ToEr(new Vector3(tg.X, tg.Y, tg.Z), roll);
                for (int i = 0; i < tanCap; i++) nv.Tangents.Add(new Vector4(tt, tg.W));
                var uv0 = v.UVs.Count > 0 ? v.UVs[0] : Vector3.Zero;
                var uv = glow ? new Vector3(0.5f, GlowBand * 0.4f, 0) : new Vector3(uv0.X, GlowBand + uv0.Y * (1 - GlowBand), 0);
                for (int i = 0; i < uvCap; i++) nv.UVs.Add(uv);
                for (int i = 0; i < colCap; i++) nv.Colors.Add(new FLVER.VertexColor((byte)255, (byte)255, (byte)255, (byte)255));
                nv.BoneIndices[0] = 0;
                nv.BoneWeights[0] = 1f;
                mesh.Vertices.Add(nv);
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            var i0 = sm.FaceSets[0].Indices.ToList();
            var i1 = sm.FaceSets.Count > 1 ? sm.FaceSets[1].Indices.ToList() : i0;
            var i2 = sm.FaceSets.Count > 2 ? sm.FaceSets[2].Indices.ToList() : i1;
            var lods = new[] { (FLVER2.FaceSet.FSFlags.None, i0), (FLVER2.FaceSet.FSFlags.LodLevel1, i1), (FLVER2.FaceSet.FSFlags.LodLevel2, i2) };
            foreach (var (f, i) in lods) mesh.FaceSets.Add(new FLVER2.FaceSet(f, false, false, 7, new List<int>(i)));
            foreach (var (f, i) in lods) mesh.FaceSets.Add(new FLVER2.FaceSet(f | FLVER2.FaceSet.FSFlags.MotionBlur, false, false, 7, new List<int>(i)));
            mesh.BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = mn, Max = mx, Unk = Vector3.Zero };
            flver.Meshes.Add(mesh);
            bbMin = Vector3.Min(bbMin, mn); bbMax = Vector3.Max(bbMax, mx);
        }
        if (blade)
        {
            var tris = new List<(Vector3 a, Vector3 b, Vector3 c)>();
            float w = bladeHalfW, t = 0.012f, y0 = bladeStart, y1 = -tipLen, ym = y0 + (y1 - y0) * 0.85f;
            Vector3[] Ring(float y, float k) => new[] { new Vector3(w * k, y, 0), new Vector3(0, y, t * k), new Vector3(-w * k, y, 0), new Vector3(0, y, -t * k) };
            var r0 = Ring(y0, 1f); var r1 = Ring(ym, 0.9f); var tip = new Vector3(0, y1, 0);
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                tris.Add((r0[i], r0[j], r1[j])); tris.Add((r0[i], r1[j], r1[i]));
                tris.Add((r1[i], r1[j], tip));
            }
            var bmesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = tplMesh.NodeIndex, UseBoneWeights = false, Dynamic = 0 };
            bmesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
            var idxs = new List<int>();
            var bmn = new Vector3(float.MaxValue); var bmx = new Vector3(float.MinValue);
            void AddV(Vector3 p, Vector3 n)
            {
                var nv = new FLVER.Vertex(uvCap, tanCap, colCap);
                nv.Positions.Add(p); nv.Normals.Add(n); nv.NormalWs.Add(0);
                for (int i = 0; i < tanCap; i++) nv.Tangents.Add(new Vector4(1, 0, 0, 1));
                var uvb = new Vector3(0.5f, GlowBand * 0.4f, 0);
                for (int i = 0; i < uvCap; i++) nv.UVs.Add(uvb);
                for (int i = 0; i < colCap; i++) nv.Colors.Add(new FLVER.VertexColor((byte)255, (byte)255, (byte)255, (byte)255));
                nv.BoneIndices[0] = 0; nv.BoneWeights[0] = 1f;
                bmesh.Vertices.Add(nv);
                bmn = Vector3.Min(bmn, p); bmx = Vector3.Max(bmx, p);
            }
            foreach (var (a, b, c) in tris)
            {
                var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                int b0 = bmesh.Vertices.Count;
                AddV(a, n); AddV(b, n); AddV(c, n);          // front
                AddV(a, -n); AddV(b, -n); AddV(c, -n);       // back (so it shows from either side whatever the culling)
                idxs.AddRange(new[] { b0, b0 + 1, b0 + 2, b0 + 3, b0 + 5, b0 + 4 });
            }
            var bl = new[] { FLVER2.FaceSet.FSFlags.None, FLVER2.FaceSet.FSFlags.LodLevel1, FLVER2.FaceSet.FSFlags.LodLevel2 };
            foreach (var f in bl) bmesh.FaceSets.Add(new FLVER2.FaceSet(f, false, false, 7, new List<int>(idxs)));
            foreach (var f in bl) bmesh.FaceSets.Add(new FLVER2.FaceSet(f | FLVER2.FaceSet.FSFlags.MotionBlur, false, false, 7, new List<int>(idxs)));
            bmesh.BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = bmn, Max = bmx, Unk = Vector3.Zero };
            flver.Meshes.Add(bmesh);
            bbMin = Vector3.Min(bbMin, bmn); bbMax = Vector3.Max(bbMax, bmx);
        }
        flver.Header.BoundingBoxMin = bbMin; flver.Header.BoundingBoxMax = bbMax;
        byte[] flverBytes = flver.Write();
        Console.WriteLine($"  weapon bbox {bbMin}..{bbMax}, {flver.Meshes.Sum(m => m.Vertices.Count)} verts");

        // ---- textures: AC6 colour-region mask + wear on the grip/body, a bright strip at the top for the energy parts ----
        var pal = Palettes[glowName];
        int rowsBody = 0;
        Rgba Load(string suffix, int w, int h)
        {
            var t = acTpf.Textures.First(x => x.Name.StartsWith(acTex, StringComparison.OrdinalIgnoreCase) && KindName(x.Name) == suffix);
            t.Headerize();
            return Rgba.FromDds(t.Bytes, tmp, w, h);
        }
        static string KindName(string n)
        {
            string x = n.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? n[..^2] : n;
            return x.EndsWith("_3m", StringComparison.OrdinalIgnoreCase) ? "3m" : x.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "a" : x.EndsWith("_n", StringComparison.OrdinalIgnoreCase) ? "n" : x.EndsWith("_em", StringComparison.OrdinalIgnoreCase) ? "em" : "?";
        }
        var tplTpf = TPF.Read(tplMain.Bytes.Length > 0 ? tplBnd.Files.First(f => f.ID == 100).Bytes : Array.Empty<byte>());
        var aTpl = tplTpf.Textures.First(t => KindOf(t.Name) == "a");
        aTpl.Headerize();
        int aw = BitConverter.ToInt32(aTpl.Bytes, 16), ah = BitConverter.ToInt32(aTpl.Bytes, 12);
        rowsBody = (int)(ah * (1 - GlowBand));
        var wearBody = Load("a", aw, rowsBody);
        var maskBody = Load("3m", aw, rowsBody);
        int glowRows = ah - rowsBody;
        var wear = new Rgba(aw, ah); var mask = new Rgba(aw, ah);
        Buffer.BlockCopy(wearBody.Px, 0, wear.Px, glowRows * aw * 4, rowsBody * aw * 4);
        Buffer.BlockCopy(maskBody.Px, 0, mask.Px, glowRows * aw * 4, rowsBody * aw * 4);
        var albedo = Paint.Albedo(wear, mask, "gunmetal");
        var metal = Paint.Metal(mask);
        for (int y = 0; y < glowRows; y++)
            for (int x = 0; x < aw; x++)
            {
                int k = (y * aw + x) * 4;
                albedo.Px[k] = pal.glow[0]; albedo.Px[k + 1] = pal.glow[1]; albedo.Px[k + 2] = pal.glow[2]; albedo.Px[k + 3] = 255;
                metal.Px[k] = metal.Px[k + 1] = metal.Px[k + 2] = 0; metal.Px[k + 3] = 255;
            }
        var medianN = Rgba.FromDds(tplTpf.Textures.First(t => KindOf(t.Name) == "n") is var nt && Header(nt) is var nb ? nb : Array.Empty<byte>(), tmp, 128, 128).Median();
        var normal = new Rgba(256, 256);
        for (int i = 0; i < normal.Px.Length; i += 4) { normal.Px[i] = medianN[0]; normal.Px[i + 1] = medianN[1]; normal.Px[i + 2] = medianN[2]; normal.Px[i + 3] = medianN[3]; }

        byte[] MakeTpf(TPF template)
        {
            var tpf = new TPF { Platform = template.Platform, Encoding = template.Encoding, Flag2 = template.Flag2 };
            foreach (var t in template.Textures)
            {
                t.Headerize();
                int w = BitConverter.ToInt32(t.Bytes, 16), h = BitConverter.ToInt32(t.Bytes, 12);
                char kind = KindOf(t.Name)[0];
                var (dxgi, srgb) = FormatFor(t.Format, kind);
                Rgba src = kind == 'a' ? albedo : kind == 'm' ? metal : normal;
                tpf.Textures.Add(new TPF.Texture(t.Name, t.Format, t.Flags1, src.Compress(dxgi, w, h, tmp, srgb), template.Platform));
            }
            return tpf.Write();
        }
        static byte[] Header(TPF.Texture t) { t.Headerize(); return t.Bytes; }

        var lowTpf = TPF.Read(lowBnd.Files.First(f => f.ID == 100).Bytes);
        WritePart(tplBnd, Path.Combine(outParts, erBase + ".partsbnd.dcx"), flverBytes, MakeTpf(tplTpf), tpl);
        WritePart(lowBnd, Path.Combine(outParts, erBase + "_l.partsbnd.dcx"), flverBytes, MakeTpf(lowTpf), tpl);
        Console.WriteLine($"  wrote {erBase} and _l");
    }

    // main model, textures replaced; any extra model (the sheath, ID 201) shrunk to one hidden triangle; everything else kept
    private static void WritePart(BND4 template, string path, byte[] flver, byte[] tpf, FLVER2 mainTemplate)
    {
        var bnd = new BND4
        {
            BigEndian = template.BigEndian, Compression = template.Compression, Format = template.Format, Unicode = template.Unicode,
            Extended = template.Extended, Unk04 = template.Unk04, Unk05 = template.Unk05, Version = template.Version,
        };
        foreach (var f in template.Files)
        {
            if (f.ID == 100) bnd.Files.Add(new BinderFile(f.Flags, f.ID, f.Name, tpf));
            else if (f.ID == 200) bnd.Files.Add(new BinderFile(f.Flags, f.ID, f.Name, flver));
            else if (f.ID == 201) bnd.Files.Add(new BinderFile(f.Flags, f.ID, f.Name, HideModel(f.Bytes)));
            else bnd.Files.Add(f);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bnd.Write(path);
    }

    /// <summary>Keeps a model's skeleton, material and layout but replaces its geometry by one tiny triangle.</summary>
    private static byte[] HideModel(byte[] bytes)
    {
        var fl = FLVER2.Read(bytes);
        var first = fl.Meshes[0];
        var layout = fl.BufferLayouts[first.VertexBuffers[0].LayoutIndex];
        int uvCap = 0, tanCap = 0, colCap = 0;
        foreach (var mem in layout)
        {
            string sem = mem.Semantic.ToString();
            if (sem == "UV") uvCap += (mem.Type.ToString().Contains('4') ? 2 : 1);
            else if (sem == "Tangent") tanCap++;
            else if (sem == "VertexColor") colCap++;
        }
        var mesh = new FLVER2.Mesh { MaterialIndex = first.MaterialIndex, NodeIndex = first.NodeIndex, UseBoneWeights = false, Dynamic = 0 };
        mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(first.VertexBuffers[0].LayoutIndex));
        var pos = new[] { Vector3.Zero, new Vector3(0.001f, 0, 0), new Vector3(0, 0.001f, 0) };
        foreach (var p in pos)
        {
            var nv = new FLVER.Vertex(uvCap, tanCap, colCap);
            nv.Positions.Add(p); nv.Normals.Add(new Vector3(0, 0, 1)); nv.NormalWs.Add(0);
            for (int i = 0; i < tanCap; i++) nv.Tangents.Add(new Vector4(1, 0, 0, 1));
            for (int i = 0; i < uvCap; i++) nv.UVs.Add(Vector3.Zero);
            for (int i = 0; i < colCap; i++) nv.Colors.Add(new FLVER.VertexColor((byte)255, (byte)255, (byte)255, (byte)255));
            nv.BoneIndices[0] = 0; nv.BoneWeights[0] = 1f;
            mesh.Vertices.Add(nv);
        }
        var idx = new List<int> { 0, 1, 2 };
        foreach (var f in new[] { FLVER2.FaceSet.FSFlags.None, FLVER2.FaceSet.FSFlags.LodLevel1, FLVER2.FaceSet.FSFlags.LodLevel2 })
            mesh.FaceSets.Add(new FLVER2.FaceSet(f, false, false, 7, new List<int>(idx)));
        foreach (var f in new[] { FLVER2.FaceSet.FSFlags.None, FLVER2.FaceSet.FSFlags.LodLevel1, FLVER2.FaceSet.FSFlags.LodLevel2 })
            mesh.FaceSets.Add(new FLVER2.FaceSet(f | FLVER2.FaceSet.FSFlags.MotionBlur, false, false, 7, new List<int>(idx)));
        mesh.BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = pos[0], Max = pos[1], Unk = Vector3.Zero };
        fl.Meshes = new List<FLVER2.Mesh> { mesh };
        fl.Header.BoundingBoxMin = pos[0]; fl.Header.BoundingBoxMax = pos[1];
        return fl.Write();
    }
}
