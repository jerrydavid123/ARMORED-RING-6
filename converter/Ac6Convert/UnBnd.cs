using SoulsFormats;

namespace Ac6Convert;

internal static class UnBnd
{
    // unbnd <oodle dir> <bnd4 file> <out folder> [name filter]   writes every file in the container (names flattened)
    public static int Run(string path, string outDir, string? filter)
    {
        var bnd = BND4.Read(path);
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var f in bnd.Files)
        {
            string name = f.ID + "_" + Path.GetFileName(f.Name.Replace('\\', '/'));
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            File.WriteAllBytes(Path.Combine(outDir, name), f.Bytes);
            n++;
        }
        Console.WriteLine($"wrote {n} files to {outDir}");
        return 0;
    }
}
