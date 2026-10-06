using System.Text;
using Havoc;
using Havoc.IO.Tagfile.Binary;
using Havoc.Objects;
using Havoc.Reflection;

// HkxTool: inspect and rewrite the Havok tagfiles (.hkx) used by Elden Ring and Armored Core VI.
//   HkxTool info <file.hkx> [file.compendium] [maxDepth]
//   HkxTool rt   <file.hkx> <file.compendium> <out.hkx> [sdk 2018|2016|2015|2019]
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: HkxTool info|rt ..."); return 1; }
        try
        {
            switch (args[0])
            {
                case "info":
                {
                    var root = Load(args[1], args.Length > 2 && args[2].Length > 0 && args[2] != "-" ? args[2] : null);
                    int depth = args.Length > 3 ? int.Parse(args[3]) : 6;
                    var sb = new StringBuilder();
                    Dump(root, "root", 0, depth, sb);
                    Console.Write(sb.ToString());
                    return 0;
                }
                case "patchtest":
                {
                    // load + save with no changes must reproduce the file byte for byte
                    var orig = File.ReadAllBytes(args[1]);
                    var tp = TagPatch.Load(orig);
                    var again = tp.Save();
                    Console.WriteLine(again.SequenceEqual(orig) ? $"identical ({orig.Length} bytes)" : $"DIFFERENT: {orig.Length} vs {again.Length}");
                    return 0;
                }
                case "port":
                    return AnimPort.Run(args);
                case "bones":
                    return ClipBones.Run(args);
                case "sheet":
                    return ClipSheet.Run(args);
                case "stats":
                    return ClipStats.Run(args);
                case "skel":
                {
                    var root = Load(args[1], null);
                    var sk = Deref(Field(Deref(Field(((HkArray)Field(root, "namedVariants")).Value[0], "variant")), "skeletons") is HkArray sa ? sa.Value[0] : null);
                    var bones = (HkArray)Field(sk, "bones");
                    var parents = ((HkArray)Field(sk, "parentIndices")).Value.Select(x => Convert.ToInt32(x.Value)).ToArray();
                    var pose = (HkArray)Field(sk, "referencePose");
                    for (int i = 0; i < bones.Value.Count; i++)
                    {
                        var t = F(Field(pose.Value[i], "translation")); var q = F(Field(pose.Value[i], "rotation")); var sc = F(Field(pose.Value[i], "scale"));
                        string name = Convert.ToString(Field(bones.Value[i], "name").Value);
                        Console.WriteLine($"{i,3} {name,-28} p={parents[i],3} t=({t[0]:0.0000},{t[1]:0.0000},{t[2]:0.0000}) q=({q[0]:0.0000},{q[1]:0.0000},{q[2]:0.0000},{q[3]:0.0000}) s=({sc[0]:0.000},{sc[1]:0.000},{sc[2]:0.000})");
                    }
                    return 0;
                }
                case "rt":
                {
                    var root = Load(args[1], args[2]);
                    var sdk = HkSdkVersion.V20180100;
                    if (args.Length > 4)
                        sdk = args[4] switch { "2015" => HkSdkVersion.V20150100, "2016" => HkSdkVersion.V20160200, "2019" => HkSdkVersion.V20190100, _ => HkSdkVersion.V20180100 };
                    HkBinaryTagfileWriter.Write(args[3], root, sdk);
                    Console.WriteLine($"wrote {args[3]} ({new FileInfo(args[3]).Length} bytes, sdk {sdk})");
                    return 0;
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("ERROR: " + e);
            return 2;
        }
        return 1;
    }

    static IHkObject Field(IHkObject o, string name) => (o as HkClass)?.Value.FirstOrDefault(kv => kv.Key.Name == name).Value;
    static IHkObject Deref(IHkObject o) => o is HkPtr p ? p.Value : o;
    static float[] F(IHkObject o) => ((HkArray)o).Value.Select(x => Convert.ToSingle(x.Value)).ToArray();

    static IHkObject Load(string hkx, string compendium)
    {
        byte[] file = File.ReadAllBytes(hkx);
        byte[] comp = compendium != null ? File.ReadAllBytes(compendium) : null;
        return HkBinaryTagfileReader.Read(file, comp);
    }

    static void Dump(IHkObject o, string name, int depth, int maxDepth, StringBuilder sb)
    {
        string pad = new string(' ', depth * 2);
        if (o == null) { sb.AppendLine($"{pad}{name}: null"); return; }
        switch (o)
        {
            case HkClass c:
                sb.AppendLine($"{pad}{name}: {c.Type.Name}");
                if (depth >= maxDepth) { sb.AppendLine($"{pad}  ..."); return; }
                foreach (var kv in c.Value)
                    Dump(kv.Value, kv.Key.Name, depth + 1, maxDepth, sb);
                break;
            case HkArray a:
            {
                IReadOnlyList<IHkObject> items = a.Value ?? new List<IHkObject>();
                bool prim = items.Count > 0 && !(items[0] is HkClass) && !(items[0] is HkPtr) && !(items[0] is HkArray);
                if (prim)
                {
                    var first = string.Join(",", items.Take(10).Select(x => Convert.ToString(x.Value, System.Globalization.CultureInfo.InvariantCulture)));
                    sb.AppendLine($"{pad}{name}: [{items.Count}] {first}{(items.Count > 10 ? ",..." : "")}");
                }
                else
                {
                    sb.AppendLine($"{pad}{name}: [{items.Count}] {a.Type.Name}");
                    if (depth >= maxDepth) return;
                    for (int i = 0; i < Math.Min(items.Count, 6); i++)
                        Dump(items[i], $"[{i}]", depth + 1, maxDepth, sb);
                    if (items.Count > 6) sb.AppendLine($"{pad}  ... ({items.Count - 6} more)");
                }
                break;
            }
            case HkPtr p:
                if (p.Value == null) sb.AppendLine($"{pad}{name}: ptr null");
                else if (depth >= maxDepth) sb.AppendLine($"{pad}{name}: ptr -> {p.Value.Type.Name}");
                else Dump(p.Value, name + " (ptr)", depth, maxDepth, sb);
                break;
            default:
                sb.AppendLine($"{pad}{name}: {Convert.ToString(o.Value, System.Globalization.CultureInfo.InvariantCulture)} ({o.Type.Name})");
                break;
        }
    }
}
