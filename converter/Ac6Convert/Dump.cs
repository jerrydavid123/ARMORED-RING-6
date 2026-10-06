using SoulsFormats;

namespace Ac6Convert;

internal static class Dump
{
    public static int Run(string partsbndPath, bool nodes)
    {
        var bnd = BND4.Read(partsbndPath);
        foreach (var f in bnd.Files)
        {
            Console.WriteLine($"FILE [{f.ID}] flags={f.Flags} {f.Name} ({f.Bytes.Length} bytes)");
            if (f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase))
            {
                var tpf = TPF.Read(f.Bytes);
                Console.WriteLine($"  TPF platform={tpf.Platform} encoding={tpf.Encoding} flag2={tpf.Flag2}");
                foreach (var t in tpf.Textures)
                    Console.WriteLine($"   tex {t.Name} fmt={t.Format} flags1={t.Flags1} bytes={t.Bytes.Length}");
            }
            if (!f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)) continue;
            var fl = FLVER2.Read(f.Bytes);
            var h = fl.Header;
            Console.WriteLine($"  FLVER v{h.Version:X} unicode={h.Unicode} bbox {h.BoundingBoxMin}..{h.BoundingBoxMax} unk4A={h.Unk4A} unk4B={h.Unk4B} unk4C={h.Unk4C} unk5C={h.Unk5C} unk5D={h.Unk5D} unk68={h.Unk68} special={h.SpecialModifier} unk74={h.Unk74}");
            Console.WriteLine($"  dummies={fl.Dummies.Count} gxlists={fl.GXLists.Count} skeletons={(fl.Skeletons != null)}");
            if (Environment.GetEnvironmentVariable("DUMP_DUMMIES") == "1")
                foreach (var d in fl.Dummies)
                    Console.WriteLine($"    dummy ref={d.ReferenceID} pos={d.Position} fwd={d.Forward} up={d.Upward} parent={d.ParentBoneIndex} attach={d.AttachBoneIndex} flag={d.Flag1}");
            for (int i = 0; i < fl.BufferLayouts.Count; i++)
                Console.WriteLine($"  layout[{i}]: " + string.Join(" ", fl.BufferLayouts[i].Select(m => $"{m.Semantic}{m.Index}:{m.Type}")));
            for (int i = 0; i < fl.Materials.Count; i++)
            {
                var m = fl.Materials[i];
                Console.WriteLine($"  mat[{i}] {m.Name} mtd={m.MTD} gx={m.GXIndex}");
                foreach (var t in m.Textures)
                    Console.WriteLine($"     {t.ParamName} -> '{t.Path}' scale={t.TilingScale}");
            }
            for (int i = 0; i < fl.Meshes.Count; i++)
            {
                var m = fl.Meshes[i];
                var v = m.Vertices.Count > 0 ? m.Vertices[0] : null;
                Console.WriteLine($"  mesh[{i}] mat={m.MaterialIndex} node={m.NodeIndex} dyn={m.Dynamic} useW={m.UseBoneWeights} verts={m.Vertices.Count} boneIdx={m.BoneIndices.Count} layouts=[{string.Join(",", m.VertexBuffers.Select(b => b.LayoutIndex))}] faceSets={m.FaceSets.Count}");
                Console.WriteLine("     facesets: " + string.Join(" | ", m.FaceSets.Select(s => $"{s.Flags}/strip={s.TriangleStrip}/cull={s.CullBackfaces}/u06={s.Unk06}/idx={s.Indices.Count}")));
                if (v != null)
                    Console.WriteLine($"     v0 pos={v.Position} w={v.BoneWeights} idx={v.BoneIndices} n={v.Normal} nw={v.NormalW} uvs={v.UVs.Count} tan={v.Tangents.Count} col={v.Colors.Count}");
            }
            if (nodes)
                for (int i = 0; i < fl.Nodes.Count; i++)
                {
                    var n = fl.Nodes[i];
                    Console.WriteLine($"  node[{i}] {n.Name} p={n.ParentIndex} t={n.Translation} r={n.Rotation} s={n.Scale} flags={n.Flags}");
                }
        }
        return 0;
    }
}

internal static class MatDump
{
    public static int Run(string bndPath, string filter)
    {
        var bnd = BND4.Read(bndPath);
        Console.WriteLine($"{bnd.Files.Count} matbins");
        int shown = 0;
        foreach (var f in bnd.Files)
        {
            if (!f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var m = MATBIN.Read(f.Bytes);
            Console.WriteLine($"[{f.ID}] {f.Name}");
            Console.WriteLine($"   shader={m.ShaderPath} source={m.SourcePath}");
            foreach (var s in m.Samplers) Console.WriteLine($"   sampler {s.Type} -> {s.Path}");
            foreach (var p in m.Params.Take(12)) Console.WriteLine($"   param {p.Name} = {p.Value}");
            if (++shown >= 12) break;
        }
        return 0;
    }
}

internal static class FmgDump
{
    // Prints "id<TAB>text" for every FMG inside a msgbnd whose file name contains the filter.
    public static int Run(string msgbndPath, string filter)
    {
        var bnd = BND4.Read(msgbndPath);
        foreach (var f in bnd.Files)
        {
            if (!f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var fmg = FMG.Read(f.Bytes);
            Console.WriteLine($"## {f.Name} ({fmg.Entries.Count})");
            foreach (var e in fmg.Entries)
                if (!string.IsNullOrWhiteSpace(e.Text)) Console.WriteLine($"{e.ID}\t{e.Text.Replace('\n', ' ')}");
        }
        return 0;
    }
}
