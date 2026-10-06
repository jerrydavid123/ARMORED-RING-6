using System.Numerics;
using SoulsFormats;

namespace Ac6Convert;

/// <summary>
/// Turns an AC6 head (hd_m_XXXX) into an Elden Ring helmet part that replaces an existing ER helmet file.
/// The ER partsbnd is the template: skeleton, material, vertex layout and file list come from it.
/// </summary>
internal static class ConvertHead
{
    // AC6 faces -Z and is ~5x human scale; ER faces +Z.
    private const float Scale = 0.23f;

    public static int Run(string erTemplatePath, string acPartPath, string outBase, string tmpDir, string paintName)
    {
        var tplBnd = BND4.Read(erTemplatePath);
        var tplFlverFile = tplBnd.Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase));
        var tpl = FLVER2.Read(tplFlverFile.Bytes);
        var tplTpf = TPF.Read(tplBnd.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);

        var acBnd = BND4.Read(acPartPath);
        var ac = FLVER2.Read(acBnd.Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
        var acTpf = TPF.Read(acBnd.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);

        // --- template pieces ---
        int headNode = tpl.Nodes.FindIndex(n => n.Name == "Head");
        int metalMat = tpl.Materials.FindIndex(m => m.MTD.Contains("_Metal", StringComparison.OrdinalIgnoreCase));
        var metalMesh = tpl.Meshes.First(m => m.MaterialIndex == metalMat);
        int layoutIndex = metalMesh.VertexBuffers[0].LayoutIndex;
        var tm = tpl.Materials[metalMat];
        Console.WriteLine($"template: head node {headNode}, metal material #{metalMat} '{tm.Name}', layout {layoutIndex}, mesh node {metalMesh.NodeIndex}");

        // --- source mesh: the AC6 HEAD material only (skip eye emissive and marking quads) ---
        var src = ac.Meshes.First(m => ac.Materials[m.MaterialIndex].Name.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"source mesh: {src.Vertices.Count} verts, {src.FaceSets[0].Indices.Count / 3} tris");

        var bbMin = new Vector3(float.MaxValue); var bbMax = new Vector3(float.MinValue);
        foreach (var v in src.Vertices) { bbMin = Vector3.Min(bbMin, v.Position); bbMax = Vector3.Max(bbMax, v.Position); }
        var acCenter = (bbMin + bbMax) / 2;
        // place the head where the template helmet sits
        var tplCenter = (tpl.Header.BoundingBoxMin + tpl.Header.BoundingBoxMax) / 2;
        var target = new Vector3(0, tplCenter.Y, tplCenter.Z);
        Console.WriteLine($"AC6 head center {acCenter}, size {bbMax - bbMin}; target {target}");

        Vector3 P(Vector3 p) { var d = p - acCenter; return new Vector3(-d.X * Scale, d.Y * Scale, -d.Z * Scale) + target; }
        Vector3 N(Vector3 n) => new Vector3(-n.X, n.Y, -n.Z);

        var mesh = new FLVER2.Mesh
        {
            MaterialIndex = 0,
            NodeIndex = metalMesh.NodeIndex,
            UseBoneWeights = true,
            Dynamic = 1,
        };
        mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
        var nMin = new Vector3(float.MaxValue); var nMax = new Vector3(float.MinValue);
        foreach (var v in src.Vertices)
        {
            var nv = new FLVER.Vertex(2, 1, 1);
            nv.Positions.Add(P(v.Position));
            nv.Normals.Add(N(v.Normal));
            nv.NormalWs.Add(0);
            var t = v.Tangents.Count > 0 ? v.Tangents[0] : new Vector4(1, 0, 0, 1);
            nv.Tangents.Add(new Vector4(-t.X, t.Y, -t.Z, t.W));
            var uv = v.UVs.Count > 0 ? v.UVs[0] : Vector3.Zero;
            nv.UVs.Add(uv);
            nv.UVs.Add(uv);
            nv.Colors.Add(new FLVER.VertexColor((byte)255, (byte)255, (byte)255, (byte)255));
            nv.BoneIndices[0] = headNode;
            nv.BoneWeights[0] = 1f;
            mesh.Vertices.Add(nv);
            nMin = Vector3.Min(nMin, nv.Position); nMax = Vector3.Max(nMax, nv.Position);
        }
        var idx = src.FaceSets[0].Indices.ToList();
        var lod1 = src.FaceSets.Count > 1 ? src.FaceSets[1].Indices.ToList() : idx;
        var lod2 = src.FaceSets.Count > 2 ? src.FaceSets[2].Indices.ToList() : lod1;
        // same six face sets ER meshes carry: LOD0/1/2, each plus its motion-blur twin
        var lods = new[] { (FLVER2.FaceSet.FSFlags.None, idx), (FLVER2.FaceSet.FSFlags.LodLevel1, lod1), (FLVER2.FaceSet.FSFlags.LodLevel2, lod2) };
        foreach (var (f, i) in lods) mesh.FaceSets.Add(new FLVER2.FaceSet(f, false, false, 7, new List<int>(i)));
        foreach (var (f, i) in lods) mesh.FaceSets.Add(new FLVER2.FaceSet(f | FLVER2.FaceSet.FSFlags.MotionBlur, false, false, 7, new List<int>(i)));
        mesh.BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = nMin, Max = nMax, Unk = Vector3.Zero };

        // --- model ---
        var flver = new FLVER2
        {
            Header = new FLVER2.FLVERHeader
            {
                BigEndian = tpl.Header.BigEndian, Version = tpl.Header.Version, Unicode = tpl.Header.Unicode,
                Unk4A = tpl.Header.Unk4A, Unk4B = tpl.Header.Unk4B, Unk4C = tpl.Header.Unk4C, Unk5C = tpl.Header.Unk5C,
                Unk5D = tpl.Header.Unk5D, Unk68 = tpl.Header.Unk68, SpecialModifier = tpl.Header.SpecialModifier, Unk74 = tpl.Header.Unk74,
                BoundingBoxMin = nMin, BoundingBoxMax = nMax,
            },
            Dummies = new List<FLVER.Dummy>(),
            GXLists = tpl.GXLists,
            Nodes = tpl.Nodes,
            Skeletons = tpl.Skeletons,
            BufferLayouts = new List<FLVER2.BufferLayout> { tpl.BufferLayouts[layoutIndex] },
        };
        var mat = new FLVER2.Material { Name = tm.Name, MTD = tm.MTD, GXIndex = tm.GXIndex };
        foreach (var t in tm.Textures) mat.Textures.Add(new FLVER2.Texture(t.ParamName, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C));
        flver.Materials.Add(mat);
        flver.Meshes.Add(mesh);
        byte[] flverBytes = flver.Write();

        // --- textures: paint the AC6 colour-region mask with a frame colour, bake the AO ---
        var ao = Rgba.FromDds(DdsOf(acTpf, "_a"), tmpDir, 1024, 1024);
        var mask = Rgba.FromDds(DdsOf(acTpf, "_3m"), tmpDir, 1024, 1024);
        var albedo = Paint.Albedo(ao, mask, paintName);
        var metal = Paint.Metal(mask);
        var tplN = Rgba.FromDds(DdsOf(tplTpf, "_n"), tmpDir, 256, 256).Median();
        var normal = new Rgba(512, 512);
        for (int i = 0; i < normal.Px.Length; i += 4) { normal.Px[i] = tplN[0]; normal.Px[i + 1] = tplN[1]; normal.Px[i + 2] = tplN[2]; normal.Px[i + 3] = tplN[3]; }
        Console.WriteLine($"neutral normal (median of template): {tplN[0]},{tplN[1]},{tplN[2]},{tplN[3]}");

        string baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(erTemplatePath)); // hd_m_1290
        string upper = baseName.ToUpperInvariant();

        byte[] tpfFull = MakeTpf(tplTpf, upper, "", albedo, metal, normal, 1024, 512, 1024, tmpDir);
        string lowTplPath = erTemplatePath.Replace(".partsbnd.dcx", "_l.partsbnd.dcx");
        var lowTpl = File.Exists(lowTplPath) ? BND4.Read(lowTplPath) : tplBnd;
        var lowTplTpf = TPF.Read(lowTpl.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);
        byte[] tpfLow = MakeTpf(lowTplTpf, upper, "_l", albedo, metal, normal, 512, 256, 512, tmpDir);

        WritePart(tplBnd, outBase + ".partsbnd.dcx", flverBytes, tpfFull);
        WritePart(lowTpl, outBase + "_l.partsbnd.dcx", flverBytes, tpfLow);
        Console.WriteLine($"wrote {outBase}.partsbnd.dcx and _l ({flverBytes.Length} bytes of model)");

        // OBJ for a quick look
        using (var w = new StreamWriter(outBase + ".obj"))
        {
            foreach (var v in mesh.Vertices) w.WriteLine($"v {v.Position.X} {v.Position.Y} {v.Position.Z}");
            foreach (var v in mesh.Vertices) w.WriteLine($"vt {v.UVs[0].X} {1 - v.UVs[0].Y}");
            var ix = mesh.FaceSets[0].Indices;
            for (int i = 0; i + 2 < ix.Count; i += 3) w.WriteLine($"f {ix[i] + 1}/{ix[i] + 1} {ix[i + 1] + 1}/{ix[i + 1] + 1} {ix[i + 2] + 1}/{ix[i + 2] + 1}");
        }
        return 0;
    }

    private static byte[] DdsOf(TPF tpf, string suffix)
    {
        var t = tpf.Textures.First(x => x.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        t.Headerize();
        return t.Bytes;
    }

    private static byte[] MakeTpf(TPF template, string upper, string lod, Rgba albedo, Rgba metal, Rgba normal, int wa, int wm, int wn, string tmpDir)
    {
        var tpf = new TPF { Platform = template.Platform, Encoding = template.Encoding, Flag2 = template.Flag2 };
        foreach (var t in template.Textures)
        {
            string nm = t.Name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? t.Name[..^2] : t.Name;
            string kind = nm[^1].ToString().ToLowerInvariant(); // a, m or n
            // template names look like HD_M_1290_a or HD_M_1290_a_l
            byte[] dds = kind switch
            {
                "a" => albedo.Compress("BC1_UNORM_SRGB", wa, wa, tmpDir, true),
                "m" => metal.Compress("BC4_UNORM", wm, wm, tmpDir, false),
                _ => normal.Compress("BC7_UNORM", wn, wn, tmpDir, false),
            };
            tpf.Textures.Add(new TPF.Texture(t.Name, t.Format, t.Flags1, dds, template.Platform));
        }
        return tpf.Write();
    }

    private static void WritePart(BND4 template, string path, byte[] flver, byte[] tpf)
    {
        var bnd = new BND4
        {
            BigEndian = template.BigEndian, Compression = template.Compression, Format = template.Format, Unicode = template.Unicode,
            Extended = template.Extended, Unk04 = template.Unk04, Unk05 = template.Unk05, Version = template.Version,
        };
        foreach (var f in template.Files)
        {
            if (f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)) bnd.Files.Add(new BinderFile(f.Flags, f.ID, f.Name, tpf));
            else if (f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)) bnd.Files.Add(new BinderFile(f.Flags, f.ID, f.Name, flver));
            // cloth files (.clm2 / .hkx) are dropped: our mesh has no cloth
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bnd.Write(path);
    }
}

/// <summary>The recoloring of AC6's runtime-painted textures into fixed Elden Ring-style maps.</summary>
internal static class Paint
{
    private static readonly Dictionary<string, (byte[] main, byte[] accent, byte[] glow)> Palettes = new()
    {
        ["gunmetal"] = (new byte[] { 104, 110, 118 }, new byte[] { 200, 96, 36 }, new byte[] { 70, 210, 235 }),
        ["sand"] = (new byte[] { 150, 130, 98 }, new byte[] { 80, 70, 60 }, new byte[] { 255, 190, 80 }),
    };

    // 0 = main paint, 1 = accent (purple in the AC6 mask), 2 = glow (green in the AC6 mask)
    public static int Region(byte r, byte g, byte b)
    {
        if (b < 90 && g > 180) return 2;
        if (g < 90 && b > 180) return 1;
        return 0;
    }

    public static Rgba Albedo(Rgba ao, Rgba mask, string palette)
    {
        var pal = Palettes[palette];
        var o = new Rgba(mask.W, mask.H);
        for (int i = 0; i < mask.W * mask.H; i++)
        {
            int k = i * 4;
            int reg = Region(mask.Px[k], mask.Px[k + 1], mask.Px[k + 2]);
            var col = reg == 0 ? pal.main : reg == 1 ? pal.accent : pal.glow;
            // AC6's _a holds dark wear/grime in RGB with its strength in alpha; the paint itself is applied at runtime
            float wear = reg == 2 ? 0f : ao.Px[k + 3] / 255f * 0.65f;
            float gray = ao.Px[k] * 2.2f;
            for (int c = 0; c < 3; c++)
                o.Px[k + c] = (byte)Math.Clamp(col[c] * (1f - wear) + gray * wear, 0f, 255f);
            o.Px[k + 3] = 255;
        }
        return o;
    }

    public static Rgba Metal(Rgba mask)
    {
        var o = new Rgba(mask.W, mask.H);
        for (int i = 0; i < mask.W * mask.H; i++)
        {
            int k = i * 4;
            int reg = Region(mask.Px[k], mask.Px[k + 1], mask.Px[k + 2]);
            byte m = reg == 0 ? (byte)150 : reg == 1 ? (byte)90 : (byte)0;
            o.Px[k] = m; o.Px[k + 1] = m; o.Px[k + 2] = m; o.Px[k + 3] = 255;
        }
        return o;
    }
}
