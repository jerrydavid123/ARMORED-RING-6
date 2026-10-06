using System.Numerics;
using SoulsFormats;

namespace Ac6Convert;

internal static class Program
{
    private const ulong Prime64 = 0x85ul;

    private static int Main(string[] args)
    {
        if (args.Length >= 5 && args[0] == "extract")
            return Extract(args[1], args[2], args[3], args[4]);
        if (args.Length >= 3 && (args[0] == "dump" || args[0] == "dumpn"))
        {
            LoadOodle(args[1]);
            return Dump.Run(args[2], args[0] == "dumpn");
        }
        if (args.Length >= 8 && args[0] == "weapons")
        {
            // weapons <ER Game dir> <AC6 Game dir> <weapons.json> <ER parts folder> <AC6 parts folder> <out parts folder> <texconv.exe>
            LoadOodle(args[2]);
            LoadOodle(args[1]);
            Rgba.Texconv = args[7];
            return ConvertWeapon.Run(args[3], args[4], args[5], args[6], Path.Combine(Path.GetTempPath(), "ac6conv"), "gunmetal");
        }
        if (args.Length >= 9 && args[0] == "kit")
        {
            // kit <ER Game dir> <AC6 Game dir> <ER parts folder> <AC6 parts folder> <out parts folder> <ER model id> <AC6 model id> <texconv.exe> [palette] [headScale]
            LoadOodle(args[2]);
            LoadOodle(args[1]);
            Rgba.Texconv = args[8];
            return ConvertSet.Run(args[3], args[4], args[5], args[6], args[7], Path.Combine(Path.GetTempPath(), "ac6conv"), args.Length > 9 ? args[9] : "gunmetal", args.Length > 10 ? float.Parse(args[10]) : 0.16f, args.Length > 11 ? args[11] : null);
        }
        if (args.Length >= 7 && args[0] == "head")
        {
            // head <ER Game dir> <AC6 Game dir> <ER template partsbnd> <AC6 head partsbnd> <out base> <texconv.exe>
            LoadOodle(args[2]);
            LoadOodle(args[1]);
            Rgba.Texconv = args[6];
            int rc = ConvertHead.Run(args[3], args[4], args[5], Path.Combine(Path.GetTempPath(), "ac6conv"), args.Length > 7 ? args[7] : "gunmetal");
            return rc;
        }
        if (args.Length >= 4 && args[0] == "fmg")
        {
            LoadOodle(args[1]);
            return FmgDump.Run(args[2], args[3]);
        }
        if (args.Length >= 5 && args[0] == "replacebnd")
        {
            LoadOodle(args[1]);
            return ReplaceBnd.Run(args[2], args[3], args[4..]);
        }
        if (args.Length >= 4 && args[0] == "unbnd")
        {
            LoadOodle(args[1]);
            return UnBnd.Run(args[2], args[3], args.Length > 4 ? args[4] : null);
        }
        if (args.Length >= 3 && args[0] == "paramdump")
        {
            Oodle.Oodle6Ptr = System.Runtime.InteropServices.NativeLibrary.Load(args[1] + "/oo2core_6_win64.dll");
            return ParamDump.Run(args[2], args[3], args.Length > 4 ? args[4] : null);
        }
        if (args.Length >= 5 && args[0] == "regpack")
        {
            Oodle.Oodle6Ptr = System.Runtime.InteropServices.NativeLibrary.Load(args[1] + "/oo2core_6_win64.dll");
            return Reg.Run(args[2], args[3], args[4], args[5..]);
        }
        if (args.Length >= 4 && args[0] == "skiptut")
        {
            Oodle.Oodle6Ptr = System.Runtime.InteropServices.NativeLibrary.Load(args[1] + "/oo2core_6_win64.dll");
            return SkipTutorial.Run(args[2], args[3]);
        }
        if (args.Length >= 3 && args[0] == "emevd")
        {
            Oodle.Oodle6Ptr = System.Runtime.InteropServices.NativeLibrary.Load("C:/program files (x86)/steam/steamapps/common/ELDEN RING/Game/oo2core_6_win64.dll");
            return EmevdDump.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
        }
        if (args.Length >= 4 && args[0] == "obj")
        {
            LoadOodle(args[1]);
            return ObjExport.Run(args[2], args[3]);
        }
        if (args.Length >= 4 && args[0] == "skel")
        {
            LoadOodle(args[1]);
            return Skel.Run(args[2], args[3]);
        }
        if (args.Length >= 4 && args[0] == "tex")
        {
            LoadOodle(args[1]);
            return TexExport.Run(args[2], args[3]);
        }
        if (args.Length >= 4 && args[0] == "matbin")
        {
            LoadOodle(args[1]);
            return MatDump.Run(args[2], args[3]);
        }
        if (args.Length >= 4 && args[0] == "render")
        {
            LoadOodle(args[1]);
            return Render.Run(args[2], args.Skip(3).ToArray());
        }
        if (args.Length >= 3 && args[0] == "nodes")
        {
            LoadOodle(args[1]);
            return Nodes.Run(args[2], args.Length > 3 ? args[3] : null);
        }
        if (args.Length >= 3 && args[0] == "inspect")
        {
            LoadOodle(args[1]);
            return Inspect(args[2]);
        }
        Console.Error.WriteLine("usage: Ac6Convert extract <ac6|er> <Game folder> <out dir> <names.txt>");
        Console.Error.WriteLine("       Ac6Convert inspect <folder holding oo2core_8_win64.dll> <file.partsbnd.dcx>");
        return 2;
    }

    // The player's own game ships the Oodle library that its archives need; load it from the game folder.
    private static void LoadOodle(string folder)
    {
        string p8 = Path.Combine(folder, "oo2core_8_win64.dll");
        if (File.Exists(p8)) Oodle.Oodle8Ptr = System.Runtime.InteropServices.NativeLibrary.Load(p8);
        string p6 = Path.Combine(folder, "oo2core_6_win64.dll");
        if (File.Exists(p6)) Oodle.Oodle6Ptr = System.Runtime.InteropServices.NativeLibrary.Load(p6);
    }

    private static int Inspect(string path)
    {
        var bnd = BND4.Read(path);
        Console.WriteLine($"{path}: {bnd.Files.Count} files, compression {bnd.Compression}");
        foreach (var f in bnd.Files)
        {
            Console.WriteLine($"  [{f.ID}] {f.Name} ({f.Bytes.Length} bytes)");
            if (!f.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase)) continue;
            var flver = FLVER2.Read(f.Bytes);
            Console.WriteLine($"    FLVER version {flver.Header.Version:X}, bones {flver.Nodes.Count}, meshes {flver.Meshes.Count}, materials {flver.Materials.Count}");
            var bb = flver.Header;
            Console.WriteLine($"    bbox min {bb.BoundingBoxMin} max {bb.BoundingBoxMax}");
            foreach (var m in flver.Meshes)
                Console.WriteLine($"    mesh mat={flver.Materials[m.MaterialIndex].Name} verts={m.Vertices.Count} faceSets={m.FaceSets.Count} bones={m.BoneIndices.Count} dynamic={m.Dynamic}");
            Console.WriteLine("    bones: " + string.Join(", ", flver.Nodes.Take(60).Select(b => b.Name)));
        }
        return 0;
    }

    private static ulong HashPath(string path)
    {
        string p = path.Trim().Replace('\\', '/').ToLowerInvariant();
        if (!p.StartsWith('/')) p = "/" + p;
        ulong h = 0;
        foreach (char c in p) h = h * Prime64 + c;
        return h;
    }

    // Raw RSA "decrypt" with the public key, block by block (that is how the game archives hide their index).
    private static byte[] DecryptIndex(string path, string pem)
    {
        var (n, e) = ParsePkcs1PublicKey(pem);
        int inBlock = (int)((n.GetBitLength() + 7) / 8);
        int outBlock = inBlock - 1;
        byte[] data = File.ReadAllBytes(path);
        using var ms = new MemoryStream();
        for (int off = 0; off < data.Length; off += inBlock)
        {
            int len = Math.Min(inBlock, data.Length - off);
            var c = new BigInteger(data.AsSpan(off, len), isUnsigned: true, isBigEndian: true);
            var m = BigInteger.ModPow(c, e, n);
            byte[] mb = m.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (mb.Length < outBlock) ms.Write(new byte[outBlock - mb.Length]);
            ms.Write(mb, Math.Max(0, mb.Length - outBlock), Math.Min(mb.Length, outBlock));
        }
        return ms.ToArray();
    }

    private static (BigInteger n, BigInteger e) ParsePkcs1PublicKey(string pem)
    {
        string b64 = string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----")).Select(l => l.Trim()));
        byte[] der = Convert.FromBase64String(b64);
        int pos = 0;
        void Expect(byte tag) { if (der[pos++] != tag) throw new InvalidDataException("bad DER"); }
        int Len()
        {
            int b = der[pos++];
            if (b < 0x80) return b;
            int cnt = b & 0x7f, v = 0;
            for (int i = 0; i < cnt; i++) v = (v << 8) | der[pos++];
            return v;
        }
        BigInteger Int()
        {
            Expect(0x02);
            int l = Len();
            var v = new BigInteger(der.AsSpan(pos, l), isUnsigned: true, isBigEndian: true);
            pos += l;
            return v;
        }
        Expect(0x30); Len();
        var n = Int();
        var e = Int();
        return (n, e);
    }

    private static int Extract(string game, string gameDir, string outDir, string namesFile)
    {
        var wanted = new Dictionary<ulong, string>();
        foreach (string line in File.ReadAllLines(namesFile))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            wanted[HashPath(t)] = t.StartsWith('/') ? t : "/" + t;
        }
        Console.WriteLine($"{wanted.Count} wanted paths");

        int found = 0;
        foreach (var kv in game == "er" ? ArchiveKeys.ErKeys : ArchiveKeys.Ac6Keys)
        {
            string archive = kv.Key;
            if (archive.Contains('\\')) continue; // sd archives are not needed
            string bhd = Path.Combine(gameDir, archive + ".bhd");
            string bdt = Path.Combine(gameDir, archive + ".bdt");
            if (!File.Exists(bhd) || !File.Exists(bdt)) { Console.WriteLine($"skip {archive}: missing"); continue; }
            var index = BHD5.Read(DecryptIndex(bhd, kv.Value), BHD5.Game.EldenRing);
            using var bdtStream = File.OpenRead(bdt);
            foreach (var bucket in index.Buckets)
                foreach (var header in bucket)
                {
                    if (!wanted.TryGetValue(header.FileNameHash, out string path)) continue;
                    string dest = outDir + path.Replace('/', Path.DirectorySeparatorChar);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.WriteAllBytes(dest, header.ReadFile(bdtStream));
                    found++;
                }
            Console.WriteLine($"{archive}: done, total extracted so far {found}");
        }
        Console.WriteLine($"extracted {found} of {wanted.Count}");
        return 0;
    }
}
