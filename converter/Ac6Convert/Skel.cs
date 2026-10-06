using System.Numerics;
using SoulsFormats;

namespace Ac6Convert;

internal static class Skel
{
    public static Matrix4x4[] WorldMatrices(List<FLVER.Node> nodes)
    {
        var w = new Matrix4x4[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var local = nodes[i].ComputeLocalTransform();
            w[i] = nodes[i].ParentIndex >= 0 ? local * w[nodes[i].ParentIndex] : local;
        }
        return w;
    }

    public static int Run(string partsbnd, string filter)
    {
        var bnd = BND4.Read(partsbnd);
        var fl = FLVER2.Read(bnd.Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
        var w = WorldMatrices(fl.Nodes);
        // which nodes carry weights
        var used = new Dictionary<int, int>();
        foreach (var m in fl.Meshes)
            foreach (var v in m.Vertices)
                for (int k = 0; k < 4; k++)
                    if (v.BoneWeights[k] > 0.01f)
                    {
                        int b = m.BoneIndices.Count > 0 ? m.BoneIndices[v.BoneIndices[k]] : v.BoneIndices[k];
                        used[b] = used.GetValueOrDefault(b) + 1;
                    }
        Console.WriteLine($"{fl.Nodes.Count} nodes, {fl.Meshes.Count} meshes; bbox {fl.Header.BoundingBoxMin}..{fl.Header.BoundingBoxMax}");
        for (int i = 0; i < fl.Nodes.Count; i++)
        {
            var n = fl.Nodes[i];
            if (filter != "" && !n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !used.ContainsKey(i)) continue;
            string par = n.ParentIndex >= 0 ? fl.Nodes[n.ParentIndex].Name : "-";
            var p = w[i].Translation;
            Console.WriteLine($"[{i,3}] {n.Name,-26} parent={par,-22} world=({p.X,7:F3},{p.Y,7:F3},{p.Z,7:F3}) verts={used.GetValueOrDefault(i)}");
        }
        for (int mi = 0; mi < fl.Meshes.Count; mi++)
        {
            var m = fl.Meshes[mi];
            Console.WriteLine($"mesh[{mi}] mat={fl.Materials[m.MaterialIndex].Name} mtd={fl.Materials[m.MaterialIndex].MTD} verts={m.Vertices.Count} bones={m.BoneIndices.Count} node={m.NodeIndex}");
        }
        return 0;
    }
}

internal static class ObjExport
{
    // Writes LOD0 geometry of every mesh in a partsbnd's flver as one OBJ (positions only).
    public static int Run(string partsbnd, string outPath)
    {
        var fl = FLVER2.Read(BND4.Read(partsbnd).Files.First(f => f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)).Bytes);
        using var w = new StreamWriter(outPath);
        int baseIdx = 1;
        for (int mi = 0; mi < fl.Meshes.Count; mi++)
        {
            var m = fl.Meshes[mi];
            w.WriteLine($"o mesh{mi}");
            foreach (var v in m.Vertices) w.WriteLine($"v {v.Position.X:F4} {v.Position.Y:F4} {v.Position.Z:F4}");
            var ix = m.FaceSets[0].Indices;
            for (int i = 0; i + 2 < ix.Count; i += 3) w.WriteLine($"f {ix[i] + baseIdx} {ix[i + 1] + baseIdx} {ix[i + 2] + baseIdx}");
            baseIdx += m.Vertices.Count;
        }
        return 0;
    }
}
