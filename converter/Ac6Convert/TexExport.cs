using SoulsFormats;

namespace Ac6Convert;

internal static class TexExport
{
    // Writes every texture of every TPF inside a partsbnd (or a bare .tpf.dcx) as a .dds file.
    public static int Run(string path, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var tpfs = new List<TPF>();
        if (path.Contains(".partsbnd", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var f in BND4.Read(path).Files.Where(f => f.Name.EndsWith(".tpf", StringComparison.OrdinalIgnoreCase)))
                tpfs.Add(TPF.Read(f.Bytes));
        }
        else tpfs.Add(TPF.Read(path));
        foreach (var tpf in tpfs)
            foreach (var t in tpf.Textures)
            {
                t.Headerize();
                string dest = Path.Combine(outDir, t.Name + ".dds");
                File.WriteAllBytes(dest, t.Bytes);
                Console.WriteLine($"{dest} ({t.Bytes.Length} bytes, fmt {t.Format})");
            }
        return 0;
    }
}
