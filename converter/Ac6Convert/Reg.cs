using SoulsFormats;
using SoulsFormats.Cryptography;

namespace Ac6Convert;

internal static class Reg
{
    // regpack <vanilla regulation.bin> <folder with edited X.param files> <out regulation.bin> <ParamName> [<ParamName>...]
    public static int Run(string vanilla, string folder, string outPath, string[] names)
    {
        var bnd = RegulationDecryptor.DecryptERRegulation(vanilla);
        foreach (string n in names)
        {
            var f = bnd.Files.FirstOrDefault(x => x.Name.EndsWith("\\" + n + ".param", StringComparison.OrdinalIgnoreCase));
            if (f == null) { Console.Error.WriteLine($"{n}.param not in regulation"); return 1; }
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, n + ".param"));
            Console.WriteLine($"{n}: {f.Bytes.Length} -> {bytes.Length} bytes");
            f.Bytes = bytes;
        }
        RegulationDecryptor.EncryptERRegulation(outPath, bnd);
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes)");
        return 0;
    }
}
