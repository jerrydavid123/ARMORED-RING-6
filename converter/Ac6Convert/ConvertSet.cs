using System.Numerics;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace Ac6Convert;

/// <summary>
/// Builds a full Elden Ring armor kit (head, body, gauntlets, legs) from one AC6 part family.
/// Each ER slot file is replaced: skeleton, material and vertex layout come from the ER template of that slot,
/// the geometry comes from the AC6 parts, re-skinned onto the ER bones.
/// </summary>
internal static class ConvertSet
{
    // One AC6 mesh source going into one ER target file.
    private sealed class Source
    {
        public FLVER2 Ac = null!;
        public List<FLVER2.Mesh> Meshes = new();
        public float Scale;
        public Vector3 AcPivot, ErPivot;
        public float VOffset, VScale = 1f;      // UV band inside the target atlas
        public bool ArmFix;                     // pull arms in to the Elden Ring shoulders
        public int Band = -1;                   // texture band (default: one per source); sources sharing a band share its texture
        public Rgba? Mask, Wear;                // filled when textures are built
    }

    // AC6 and Elden Ring both face -Z with the character's left on +X (ER: toes at -Z of the ankle, chest at -Z of the spine),
    // so no turn is needed. (Kept as a function so every place that used it stays explicit.)
    private static Vector3 Turn(Vector3 v) => v;

    public static int Run(string erParts, string acParts, string outParts, string erId, string acId, string tmp, string palette, float headScale, string? shoulder = null)
    {
        // ---- load AC6 parts ----
        FLVER2 LoadAc(string slot) => FLVER2.Read(BND4.Read(Path.Combine(acParts, $"{slot}_m_{acId}.partsbnd.dcx")).Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
        TPF LoadAcTpf(string slot) => TPF.Read(BND4.Read(Path.Combine(acParts, $"{slot}_m_{acId}.partsbnd.dcx")).Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);
        var acHd = LoadAc("hd"); var acBd = LoadAc("bd"); var acAm = LoadAc("am"); var acLg = LoadAc("lg");

        static IEnumerable<FLVER2.Mesh> Paintable(FLVER2 f) =>
            f.Meshes.Where(m =>
            {
                string mtd = f.Materials[m.MaterialIndex].MTD;
                return mtd.Contains("[CM]", StringComparison.OrdinalIgnoreCase) || mtd.Contains("_Limiter", StringComparison.OrdinalIgnoreCase);
            });

        // head: centre the AC6 head on the ER head
        var hdVerts = Paintable(acHd).SelectMany(m => m.Vertices).Select(v => v.Position).ToList();
        var hdMin = hdVerts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var hdMax = hdVerts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var srcHd = new Source { Ac = acHd, Meshes = Paintable(acHd).ToList(), Scale = headScale, AcPivot = (hdMin + hdMax) / 2,
            ErPivot = new Vector3(0, 1.50f + (hdMax.Y - hdMin.Y) * headScale / 2, 0.03f) };
        // core and arms share one pivot: AC6 Spine1 -> ER Spine2
        var bodyPivotAc = new Vector3(0, 8.466f, 0);
        var bodyPivotEr = new Vector3(0, 1.239f, 0);
        var srcBd = new Source { Ac = acBd, Meshes = Paintable(acBd).ToList(), Scale = 0.15f, AcPivot = bodyPivotAc, ErPivot = bodyPivotEr, VOffset = 0f, VScale = 0.5f };
        var srcAm = new Source { Ac = acAm, Meshes = Paintable(acAm).ToList(), Scale = 0.15f, AcPivot = bodyPivotAc, ErPivot = bodyPivotEr, VOffset = 0.5f, VScale = 0.5f, ArmFix = true };
        var srcLg = new Source { Ac = acLg, Meshes = Paintable(acLg).ToList(), Scale = 0.1416f, AcPivot = Vector3.Zero, ErPivot = Vector3.Zero };

        // textures (AC6 colour-region mask + wear) per source
        void Tex(Source s, string slot, int w, int h)
        {
            var tpf = slot.StartsWith("wp:") ? TPF.Read(Path.Combine(acParts, $"wp_{slot[3..]}.tpf.dcx")) : LoadAcTpf(slot);
            s.Wear = Rgba.FromDds(DdsOf(tpf, "_a"), tmp, w, h);
            s.Mask = Rgba.FromDds(DdsOf(tpf, "_3m"), tmp, w, h);
        }

        // back weapons (AC6 shoulder mounts) ride on the body piece: rigid on the torso, placed at the body's own attach points
        var bdSources = new List<Source> { srcBd, srcAm };
        var bdTexSlots = new List<string> { "bd", "am" };
        if (shoulder != null)
        {
            Vector3 ApPos(FLVER2 bd, string apName, out Matrix4x4 world)
            {
                var w = new Matrix4x4[bd.Nodes.Count];
                for (int i = 0; i < bd.Nodes.Count; i++)
                {
                    var loc = bd.Nodes[i].ComputeLocalTransform();
                    w[i] = bd.Nodes[i].ParentIndex >= 0 ? loc * w[bd.Nodes[i].ParentIndex] : loc;
                }
                int k = bd.Nodes.FindIndex(n => n.Name == apName);
                world = w[k];
                return w[k].Translation;
            }
            int band = 2;
            foreach (var (side, ap) in new[] { ("r", "AP_BD_8020_053"), ("l", "AP_BD_8020_055") })
            {
                var wp = FLVER2.Read(BND4.Read(Path.Combine(acParts, $"wp_{side}_{shoulder}.partsbnd.dcx")).Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
                ApPos(acBd, ap, out var m);
                // weapon-local -> AC6 body space through the attach point (row-vector convention)
                foreach (var mesh in wp.Meshes)
                    foreach (var v in mesh.Vertices)
                    {
                        v.Position = Vector3.Transform(v.Position, m);
                        v.Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, m));
                        for (int t = 0; t < v.Tangents.Count; t++) { var tg = v.Tangents[t]; var tv = Vector3.TransformNormal(new Vector3(tg.X, tg.Y, tg.Z), m); v.Tangents[t] = new Vector4(tv, tg.W); }
                    }
                var src = new Source { Ac = wp, Meshes = wp.Meshes.Where(me => me.Vertices.Count > 100).ToList(), Scale = 0.15f, AcPivot = bodyPivotAc, ErPivot = bodyPivotEr, Band = band };
                bdSources.Add(src);
                bdTexSlots.Add("wp:" + shoulder);
                Console.WriteLine($"shoulder weapon {side}: {src.Meshes.Sum(me => me.Vertices.Count)} verts at {ap}");
            }
            int bands = 3;
            srcBd.VOffset = 0f; srcBd.VScale = 1f / bands;
            srcAm.VOffset = 1f / bands; srcAm.VScale = 1f / bands;
            foreach (var src in bdSources.Skip(2)) { src.VOffset = 2f / bands; src.VScale = 1f / bands; }
        }

        Directory.CreateDirectory(outParts);
        BuildTarget("hd", erParts, outParts, erId, tmp, palette, new List<Source> { srcHd }, Tex);
        BuildTarget("bd", erParts, outParts, erId, tmp, palette, bdSources, Tex, bdTexSlots.ToArray());
        BuildTarget("lg", erParts, outParts, erId, tmp, palette, new List<Source> { srcLg }, Tex);
        BuildStub("am", erParts, outParts, erId, tmp, palette);
        return 0;
    }

    private static byte[] DdsOf(TPF tpf, string suffix)
    {
        var t = tpf.Textures.First(x =>
        {
            string n = x.Name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? x.Name[..^2] : x.Name;
            return n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        });
        t.Headerize();
        return t.Bytes;
    }

    private static bool HasTex(TPF tpf, string suffix) => tpf.Textures.Any(x =>
    {
        string n = x.Name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? x.Name[..^2] : x.Name;
        return n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    });

    private static (int w, int h) DdsDims(byte[] dds) => (BitConverter.ToInt32(dds, 16), BitConverter.ToInt32(dds, 12));


    /// <summary>The template material our paint goes on: prefer Metal, then Leather, then Fabric, never cloth/fur/shell variants.</summary>
    private static int PickMaterial(FLVER2 tpl)
    {
        foreach (string kind in new[] { "_Metal.matxml", "_Leather.matxml", "_Fabric.matxml", "_Belt.matxml" })
        {
            int i = tpl.Materials.FindIndex(m => m.MTD.EndsWith(kind, StringComparison.OrdinalIgnoreCase)
                && tpl.Meshes.Any(me => me.MaterialIndex == tpl.Materials.IndexOf(m) && me.VertexBuffers.Count == 1));
            if (i >= 0) return i;
        }
        throw new InvalidOperationException("no usable material in template");
    }

    // ---------------- bone mapping ----------------
    private static readonly Regex FingerRx = new(@"^Finger(\d)(\d?)$", RegexOptions.Compiled);

    private static List<string>? Candidates(string rest)
    {
        if (rest.StartsWith("Clavicle") || rest == "ArmJoint") return new() { "Clavicle" };
        if (rest.StartsWith("Shoulder")) return new() { "Shoulder", "Clavicle" };
        if (rest == "UpperArm" || rest.StartsWith("UpArm")) return new() { "UpperArm", "Clavicle" };
        if (rest.StartsWith("ForeArm") || rest == "Elbow") return new() { "Forearm", "UpperArm" };
        if (rest == "Wrist" || rest.StartsWith("HandTwist") || rest == "Hand") return new() { "Hand", "Forearm", "UpperArm" };
        var fm = FingerRx.Match(rest);
        if (fm.Success) return new() { "Finger" + fm.Groups[1].Value + fm.Groups[2].Value, "Finger" + fm.Groups[1].Value, "Hand", "Forearm", "UpperArm" };
        if (rest.StartsWith("Thigh") || rest == "Coxa") return new() { "Thigh", "Pelvis" };
        if (rest.StartsWith("Calf") || rest.StartsWith("Knee")) return new() { "Calf", "Thigh", "Pelvis" };
        if (rest.StartsWith("Foot")) return new() { "Foot", "Calf", "Thigh" };
        if (rest.StartsWith("Toe") || rest.StartsWith("OffsetToe")) return new() { "Toe0", "Foot", "Calf" };
        return null;
    }

    private static List<string>? Unsided(string name) => name switch
    {
        "Spine" => new() { "Spine1", "Spine" },
        "Spine1" => new() { "Spine2", "Spine1" },
        "Neck" => new() { "Neck", "Spine2" },
        "Head" => new() { "Head", "Neck" },
        _ when name.StartsWith("Pelvis") => new() { "Pelvis" },
        _ => null,
    };

    private static string? ErNameFor(string acName, HashSet<string> target)
    {
        string side = acName.StartsWith("L_") ? "L_" : acName.StartsWith("R_") ? "R_" : "";
        string rest = side == "" ? acName : acName[2..];
        string swapped = side;   // same side: no turn, so L stays L
        var list = side == "" ? Unsided(rest) : Candidates(rest);
        if (list == null) return null;
        foreach (var c in list)
        {
            string n = swapped + c;
            if (target.Contains(n)) return n;
        }
        return null;
    }

    private static int[] BuildBoneMap(FLVER2 ac, List<FLVER.Node> targetNodes, HashSet<string> usable, string fallback)
    {
        var map = new int[ac.Nodes.Count];
        for (int i = 0; i < ac.Nodes.Count; i++)
        {
            string? er = null;
            int cur = i;
            while (cur >= 0 && er == null)
            {
                er = ErNameFor(ac.Nodes[cur].Name, usable);
                cur = ac.Nodes[cur].ParentIndex;
            }
            er ??= fallback;
            map[i] = targetNodes.FindIndex(n => n.Name == er);
        }
        return map;
    }

    // ---------------- target build ----------------
    private static void BuildTarget(string slot, string erParts, string outParts, string erId, string tmp, string palette,
        List<Source> sources, Action<Source, string, int, int> tex, string[]? texSlots = null)
    {
        string tplPath = Path.Combine(erParts, $"{slot}_m_{erId}.partsbnd.dcx");
        var tplBnd = BND4.Read(tplPath);
        var tpl = FLVER2.Read(tplBnd.Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
        var tplTpf = TPF.Read(tplBnd.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);

        int metalMat = PickMaterial(tpl);
        var metalMesh = tpl.Meshes.First(m => m.MaterialIndex == metalMat);
        var layout = tpl.BufferLayouts[metalMesh.VertexBuffers[0].LayoutIndex];
        var tm = tpl.Materials[metalMat];

        // bones that really carry weights in the template are the ones with a valid bind pose
        var usedIdx = new HashSet<int>();
        foreach (var m in tpl.Meshes)
            foreach (var v in m.Vertices)
                for (int k = 0; k < 4; k++)
                    if (v.BoneWeights[k] > 0.01f) usedIdx.Add(v.BoneIndices[k]);
        var usable = usedIdx.Select(i => tpl.Nodes[i].Name).Where(n => !n.StartsWith("[cloth]")).ToHashSet();
        string fallback = slot switch { "hd" => "Head", "bd" => "Spine2", _ => "Pelvis" };
        if (!usable.Contains(fallback)) fallback = usable.First();

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
            GXLists = tpl.GXLists,
            Nodes = tpl.Nodes,
            Skeletons = tpl.Skeletons,
            BufferLayouts = new List<FLVER2.BufferLayout> { layout },
        };
        var mat = new FLVER2.Material { Name = tm.Name, MTD = tm.MTD, GXIndex = tm.GXIndex };
        foreach (var t in tm.Textures) mat.Textures.Add(new FLVER2.Texture(t.ParamName, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C));
        flver.Materials.Add(mat);

        var bbMin = new Vector3(float.MaxValue); var bbMax = new Vector3(float.MinValue);
        foreach (var src in sources)
        {
            var map = BuildBoneMap(src.Ac, tpl.Nodes, usable, fallback);
            ArmRetarget? arms = null;
            if (src.ArmFix)
            {
                // joint positions of the player skeleton are the same for every armor set, so use the Warrior files
                // (1290) as the reference: other sets store their arm nodes differently
                FLVER2 RefFlver(string slot) => FLVER2.Read(BND4.Read(Path.Combine(erParts, $"{slot}_m_1290.partsbnd.dcx")).Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
                arms = new ArmRetarget(src.Ac, q => Turn(q - src.AcPivot) * src.Scale + src.ErPivot, RefFlver("bd"), RefFlver("am"));
            }
            foreach (var sm in src.Meshes)
            {
                var mesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = metalMesh.NodeIndex, UseBoneWeights = true, Dynamic = 1 };
                mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var v in sm.Vertices)
                {
                    var p = Turn(v.Position - src.AcPivot) * src.Scale + src.ErPivot;
                    // influences -> ER bones
                    var acc = new Dictionary<int, float>();
                    for (int k = 0; k < 4; k++)
                    {
                        float w = v.BoneWeights[k];
                        if (w <= 0.001f) continue;
                        int ab = sm.BoneIndices.Count > 0 ? sm.BoneIndices[v.BoneIndices[k]] : v.BoneIndices[k];
                        int eb = map[ab];
                        acc[eb] = acc.GetValueOrDefault(eb) + w;
                    }
                    if (acc.Count == 0) acc[map[sm.NodeIndex >= 0 ? sm.NodeIndex : 0]] = 1f;
                    var top = acc.OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float sum = top.Sum(kv => kv.Value);

                    var normal0 = Turn(v.Normal);
                    if (arms != null) arms.Apply(v, sm, p, normal0, out p, out normal0);

                    var nv = new FLVER.Vertex(uvCap, tanCap, colCap);
                    nv.Positions.Add(p);
                    nv.Normals.Add(normal0);
                    nv.NormalWs.Add(0);
                    var t = v.Tangents.Count > 0 ? v.Tangents[0] : new Vector4(1, 0, 0, 1);
                    for (int i = 0; i < tanCap; i++) nv.Tangents.Add(t);
                    var uv0 = v.UVs.Count > 0 ? v.UVs[0] : Vector3.Zero;
                    var uv = new Vector3(uv0.X, src.VOffset + uv0.Y * src.VScale, 0);
                    for (int i = 0; i < uvCap; i++) nv.UVs.Add(uv);
                    for (int i = 0; i < colCap; i++) nv.Colors.Add(new FLVER.VertexColor((byte)255, (byte)255, (byte)255, (byte)255));
                    for (int k = 0; k < 4; k++)
                    {
                        nv.BoneIndices[k] = k < top.Count ? top[k].Key : 0;
                        nv.BoneWeights[k] = k < top.Count ? top[k].Value / sum : 0f;
                    }
                    mesh.Vertices.Add(nv);
                    mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                }
                var idx0 = sm.FaceSets[0].Indices.ToList();
                var idx1 = sm.FaceSets.Count > 1 ? sm.FaceSets[1].Indices.ToList() : idx0;
                var idx2 = sm.FaceSets.Count > 2 ? sm.FaceSets[2].Indices.ToList() : idx1;
                var lods = new[] { (FLVER2.FaceSet.FSFlags.None, idx0), (FLVER2.FaceSet.FSFlags.LodLevel1, idx1), (FLVER2.FaceSet.FSFlags.LodLevel2, idx2) };
                foreach (var (f, i) in lods) mesh.FaceSets.Add(new FLVER2.FaceSet(f, false, false, 7, new List<int>(i)));
                foreach (var (f, i) in lods) mesh.FaceSets.Add(new FLVER2.FaceSet(f | FLVER2.FaceSet.FSFlags.MotionBlur, false, false, 7, new List<int>(i)));
                mesh.BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = mn, Max = mx, Unk = Vector3.Zero };
                flver.Meshes.Add(mesh);
                bbMin = Vector3.Min(bbMin, mn); bbMax = Vector3.Max(bbMax, mx);
            }
            Console.WriteLine($"  {slot}: source with {src.Meshes.Sum(m => m.Vertices.Count)} verts, scale {src.Scale}");
        }
        flver.Header.BoundingBoxMin = bbMin; flver.Header.BoundingBoxMax = bbMax;
        Console.WriteLine($"{slot}: bbox {bbMin}..{bbMax}, {flver.Meshes.Count} meshes");
        byte[] flverBytes = flver.Write();

        // ---- textures: dims and formats come from the ER template ----
        string upper = $"{slot}_M_{erId}".ToUpperInvariant();
        byte[] Build(TPF template)
        {
            byte[] DdsOfTpl(string suffix) => DdsOf(template, suffix);
            string[] slotsForTex = texSlots ?? new[] { slot };
            var aDims = DdsDims(DdsOfTpl("_a")); var mDims = DdsDims(DdsOfTpl("_m")); var nDims = DdsDims(DdsOfTpl("_n"));
            // atlas size = the _a size of the full-size template; LOD variant reuses the same generated pixels
            return Array.Empty<byte>();
        }

        // textures at the template's full size; the low variant derives from the same pixels
        var fullA = DdsDims(DdsOf(tplTpf, "_a")); var fullM = HasTex(tplTpf, "_m") ? DdsDims(DdsOf(tplTpf, "_m")) : (w: 4, h: 4); var fullN = DdsDims(DdsOf(tplTpf, "_n"));
        // one texture band per distinct Band (default: one per source)
        int BandOf(int i) => sources[i].Band >= 0 ? sources[i].Band : i;
        int bands = Enumerable.Range(0, sources.Count).Max(BandOf) + 1;
        int aw = fullA.w, ah = fullA.h;
        var maskAtlas = new Rgba(aw, ah); var wearAtlas = new Rgba(aw, ah);
        for (int b = 0; b < bands; b++)
        {
            int si = Enumerable.Range(0, sources.Count).First(i => BandOf(i) == b);
            string texSlot = texSlots != null ? texSlots[si] : slot;
            tex(sources[si], texSlot, aw, ah / bands);
            int rows = ah / bands;
            Buffer.BlockCopy(sources[si].Mask!.Px, 0, maskAtlas.Px, b * rows * aw * 4, rows * aw * 4);
            Buffer.BlockCopy(sources[si].Wear!.Px, 0, wearAtlas.Px, b * rows * aw * 4, rows * aw * 4);
        }
        var albedo = Paint.Albedo(wearAtlas, maskAtlas, palette);
        var metal = Paint.Metal(maskAtlas);
        var tplN = Rgba.FromDds(DdsOf(tplTpf, "_n"), tmp, 128, 128).Median();
        var normal = new Rgba(256, 256);
        for (int i = 0; i < normal.Px.Length; i += 4) { normal.Px[i] = tplN[0]; normal.Px[i + 1] = tplN[1]; normal.Px[i + 2] = tplN[2]; normal.Px[i + 3] = tplN[3]; }

        byte[] MakeTpf(TPF template, int div)
        {
            var tpf = new TPF { Platform = template.Platform, Encoding = template.Encoding, Flag2 = template.Flag2 };
            foreach (var t in template.Textures)
            {
                string nm = t.Name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? t.Name[..^2] : t.Name;
                char kind = char.ToLowerInvariant(nm[^1]);
                byte[] dds = kind switch
                {
                    'a' => albedo.Compress("BC1_UNORM_SRGB", fullA.w / div, fullA.h / div, tmp, true),
                    'm' => metal.Compress("BC4_UNORM", fullM.w / div, fullM.h / div, tmp, false),
                    _ => normal.Compress("BC7_UNORM", fullN.w / div, fullN.h / div, tmp, false),
                };
                tpf.Textures.Add(new TPF.Texture(t.Name, t.Format, t.Flags1, dds, template.Platform));
            }
            return tpf.Write();
        }
        // low-detail variant: its own template (texture names end in _l), half-size textures
        string lowPath = tplPath.Replace(".partsbnd.dcx", "_l.partsbnd.dcx");
        var lowBnd = File.Exists(lowPath) ? BND4.Read(lowPath) : tplBnd;
        var lowTpf = TPF.Read(lowBnd.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);
        var lowA = DdsDims(DdsOf(lowTpf, "_a"));
        int lowDiv = Math.Max(1, fullA.w / Math.Max(1, lowA.w));

        WritePart(tplBnd, Path.Combine(outParts, $"{slot}_m_{erId}.partsbnd.dcx"), flverBytes, MakeTpf(tplTpf, 1));
        WritePart(lowBnd, Path.Combine(outParts, $"{slot}_m_{erId}_l.partsbnd.dcx"), flverBytes, MakeTpf(lowTpf, lowDiv));
        Console.WriteLine($"{slot}: wrote full + low parts ({flverBytes.Length} bytes model)");
    }

    /// <summary>Gauntlets: all arm geometry sits in the body piece, so this slot gets one hidden triangle on the forearm.</summary>
    private static void BuildStub(string slot, string erParts, string outParts, string erId, string tmp, string palette)
    {
        string tplPath = Path.Combine(erParts, $"{slot}_m_{erId}.partsbnd.dcx");
        var tplBnd = BND4.Read(tplPath);
        var tpl = FLVER2.Read(tplBnd.Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
        var tplTpf = TPF.Read(tplBnd.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);
        int metalMat = PickMaterial(tpl);
        var metalMesh = tpl.Meshes.First(m => m.MaterialIndex == metalMat);
        var layout = tpl.BufferLayouts[metalMesh.VertexBuffers[0].LayoutIndex];
        var tm = tpl.Materials[metalMat];
        int forearm = tpl.Nodes.FindIndex(n => n.Name == "L_Forearm");
        int uvCap = 0, tanCap = 0, colCap = 0;
        foreach (var mem in layout)
        {
            string sem = mem.Semantic.ToString();
            if (sem == "UV") uvCap += (mem.Type.ToString().Contains('4') ? 2 : 1);
            else if (sem == "Tangent") tanCap++;
            else if (sem == "VertexColor") colCap++;
        }
        var mesh = new FLVER2.Mesh { MaterialIndex = 0, NodeIndex = metalMesh.NodeIndex, UseBoneWeights = true, Dynamic = 1 };
        mesh.VertexBuffers.Add(new FLVER2.VertexBuffer(0));
        var pos = new[] { new Vector3(0.40f, 1.15f, 0.03f), new Vector3(0.401f, 1.15f, 0.03f), new Vector3(0.40f, 1.151f, 0.03f) };
        foreach (var p in pos)
        {
            var nv = new FLVER.Vertex(uvCap, tanCap, colCap);
            nv.Positions.Add(p); nv.Normals.Add(new Vector3(0, 0, 1)); nv.NormalWs.Add(0);
            for (int i = 0; i < tanCap; i++) nv.Tangents.Add(new Vector4(1, 0, 0, 1));
            for (int i = 0; i < uvCap; i++) nv.UVs.Add(Vector3.Zero);
            for (int i = 0; i < colCap; i++) nv.Colors.Add(new FLVER.VertexColor((byte)255, (byte)255, (byte)255, (byte)255));
            nv.BoneIndices[0] = forearm; nv.BoneWeights[0] = 1f;
            mesh.Vertices.Add(nv);
        }
        var idx = new List<int> { 0, 1, 2 };
        foreach (var f in new[] { FLVER2.FaceSet.FSFlags.None, FLVER2.FaceSet.FSFlags.LodLevel1, FLVER2.FaceSet.FSFlags.LodLevel2 })
            mesh.FaceSets.Add(new FLVER2.FaceSet(f, false, false, 7, new List<int>(idx)));
        foreach (var f in new[] { FLVER2.FaceSet.FSFlags.None, FLVER2.FaceSet.FSFlags.LodLevel1, FLVER2.FaceSet.FSFlags.LodLevel2 })
            mesh.FaceSets.Add(new FLVER2.FaceSet(f | FLVER2.FaceSet.FSFlags.MotionBlur, false, false, 7, new List<int>(idx)));
        mesh.BoundingBox = new FLVER2.Mesh.BoundingBoxes { Min = pos[0], Max = pos[1], Unk = Vector3.Zero };
        var flver = new FLVER2
        {
            Header = new FLVER2.FLVERHeader
            {
                BigEndian = tpl.Header.BigEndian, Version = tpl.Header.Version, Unicode = tpl.Header.Unicode,
                Unk4A = tpl.Header.Unk4A, Unk4B = tpl.Header.Unk4B, Unk4C = tpl.Header.Unk4C, Unk5C = tpl.Header.Unk5C,
                Unk5D = tpl.Header.Unk5D, Unk68 = tpl.Header.Unk68, SpecialModifier = tpl.Header.SpecialModifier, Unk74 = tpl.Header.Unk74,
                BoundingBoxMin = pos[0], BoundingBoxMax = pos[1],
            },
            Dummies = new List<FLVER.Dummy>(), GXLists = tpl.GXLists, Nodes = tpl.Nodes, Skeletons = tpl.Skeletons,
            BufferLayouts = new List<FLVER2.BufferLayout> { layout },
        };
        var mat = new FLVER2.Material { Name = tm.Name, MTD = tm.MTD, GXIndex = tm.GXIndex };
        foreach (var t in tm.Textures) mat.Textures.Add(new FLVER2.Texture(t.ParamName, t.Path, t.TilingScale, t.TilingTypeU, t.TilingTypeV, t.Unk14, t.Unk18, t.Unk1C));
        flver.Materials.Add(mat);
        flver.Meshes.Add(mesh);
        byte[] flverBytes = flver.Write();

        // tiny textures keep the file small; formats and names still match the template
        byte[] MakeTpf(TPF template)
        {
            var tpf = new TPF { Platform = template.Platform, Encoding = template.Encoding, Flag2 = template.Flag2 };
            var flat = new Rgba(4, 4);
            for (int i = 0; i < flat.Px.Length; i += 4) { flat.Px[i] = 90; flat.Px[i + 1] = 95; flat.Px[i + 2] = 100; flat.Px[i + 3] = 255; }
            foreach (var t in template.Textures)
            {
                string nm = t.Name.EndsWith("_l", StringComparison.OrdinalIgnoreCase) ? t.Name[..^2] : t.Name;
                char kind = char.ToLowerInvariant(nm[^1]);
                byte[] dds = kind switch
                {
                    'a' => flat.Compress("BC1_UNORM_SRGB", 4, 4, tmp, true),
                    'm' => flat.Compress("BC4_UNORM", 4, 4, tmp, false),
                    _ => flat.Compress("BC7_UNORM", 4, 4, tmp, false),
                };
                tpf.Textures.Add(new TPF.Texture(t.Name, t.Format, t.Flags1, dds, template.Platform));
            }
            return tpf.Write();
        }
        string lowPath = tplPath.Replace(".partsbnd.dcx", "_l.partsbnd.dcx");
        var lowBnd = File.Exists(lowPath) ? BND4.Read(lowPath) : tplBnd;
        var lowTpf = TPF.Read(lowBnd.Files.First(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)).Bytes);
        WritePart(tplBnd, Path.Combine(outParts, $"{slot}_m_{erId}.partsbnd.dcx"), flverBytes, MakeTpf(tplTpf));
        WritePart(lowBnd, Path.Combine(outParts, $"{slot}_m_{erId}_l.partsbnd.dcx"), flverBytes, MakeTpf(lowTpf));
        Console.WriteLine($"{slot}: wrote stub gauntlets");
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
            // cloth / animation side files are dropped: the converted mesh has no cloth
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bnd.Write(path);
    }
}
