using System.Numerics;
using System.Text.RegularExpressions;
using SoulsFormats;

namespace Ac6Convert;

/// <summary>`nodes &lt;oodle dir&gt; &lt;partsbnd&gt; [regex]`: every skeleton node of the part's flver with its parent and world position.</summary>
internal static class Nodes
{
    public static int Run(string path, string? filter)
    {
        var bnd = BND4.Read(path);
        var rx = filter != null ? new Regex(filter, RegexOptions.IgnoreCase) : null;
        foreach (var f in bnd.Files)
        {
            if (!f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)) continue;
            var fl = FLVER2.Read(f.Bytes);
            var world = new Matrix4x4[fl.Nodes.Count];
            for (int i = 0; i < fl.Nodes.Count; i++)
            {
                var n = fl.Nodes[i];
                var local = n.ComputeLocalTransform();
                world[i] = n.ParentIndex >= 0 ? local * world[n.ParentIndex] : local;
            }
            for (int i = 0; i < fl.Nodes.Count; i++)
            {
                var n = fl.Nodes[i];
                if (rx != null && !rx.IsMatch(n.Name)) continue;
                var p = world[i].Translation;
                string par = n.ParentIndex >= 0 ? fl.Nodes[n.ParentIndex].Name : "-";
                var ax = Vector3.TransformNormal(Vector3.UnitX, world[i]); var ay = Vector3.TransformNormal(Vector3.UnitY, world[i]); var az = Vector3.TransformNormal(Vector3.UnitZ, world[i]);
                Console.WriteLine($"{i,3} {n.Name,-28} parent {par,-24} world ({p.X,8:0.000},{p.Y,8:0.000},{p.Z,8:0.000}) X({ax.X:0.00},{ax.Y:0.00},{ax.Z:0.00}) Y({ay.X:0.00},{ay.Y:0.00},{ay.Z:0.00}) Z({az.X:0.00},{az.Y:0.00},{az.Z:0.00})");
            }
        }
        return 0;
    }
}
