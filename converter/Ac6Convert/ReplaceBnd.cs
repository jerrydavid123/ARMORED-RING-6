using SoulsFormats;

namespace Ac6Convert;

internal static class ReplaceBnd
{
    // replacebnd <oodle dir> <bnd4 in> <bnd4 out> <name suffix>=<replacement file> [<name suffix>=<replacement file> ...]
    // Replaces the bytes of every file in the container whose name ends with the suffix; everything else is kept as is.
    public static int Run(string inPath, string outPath, string[] pairs)
    {
        var bnd = BND4.Read(inPath);
        int replaced = 0;
        foreach (var pair in pairs)
        {
            int eq = pair.IndexOf('=');
            string suffix = pair[..eq];
            string file = pair[(eq + 1)..];
            byte[] bytes = File.ReadAllBytes(file);
            bool any = false;
            foreach (var f in bnd.Files)
            {
                if (f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    f.Bytes = bytes;
                    any = true;
                    replaced++;
                    Console.WriteLine($"replaced {f.Name} ({bytes.Length} bytes)");
                }
            }
            if (!any) Console.Error.WriteLine($"no file ends with {suffix}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        bnd.Write(outPath);
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes, {replaced} replaced)");
        return replaced > 0 ? 0 : 1;
    }
}
