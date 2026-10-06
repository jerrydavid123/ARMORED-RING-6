using System.Diagnostics;

namespace Ac6Convert;

/// <summary>Raw RGBA8 image plus helpers that go through Microsoft's texconv for the compressed formats.</summary>
internal sealed class Rgba
{
    public int W, H;
    public byte[] Px; // R,G,B,A per pixel

    public Rgba(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }

    public static string Texconv = "texconv.exe";

    private static void Run(string args)
    {
        var psi = new ProcessStartInfo(Texconv, "-nologo -y " + args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"texconv {args} failed: {o}");
    }

    /// <summary>Decode any DDS into RGBA8 of the requested size (0 = keep).</summary>
    public static Rgba FromDds(byte[] dds, string tmpDir, int w = 0, int h = 0)
    {
        Directory.CreateDirectory(tmpDir);
        string src = Path.Combine(tmpDir, "in_" + Guid.NewGuid().ToString("N") + ".dds");
        File.WriteAllBytes(src, dds);
        string outDir = Path.Combine(tmpDir, "out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        string size = w > 0 ? $"-w {w} -h {h} " : "";
        Run($"-ft dds -f R8G8B8A8_UNORM -m 1 {size}-o \"{outDir}\" \"{src}\"");
        byte[] raw = File.ReadAllBytes(Directory.GetFiles(outDir, "*.dds")[0]);
        File.Delete(src);
        Directory.Delete(outDir, true);
        int ph = BitConverter.ToInt32(raw, 12), pw = BitConverter.ToInt32(raw, 16);
        bool dx10 = raw[84] == 'D' && raw[85] == 'X' && raw[86] == '1' && raw[87] == '0';
        int off = dx10 ? 148 : 128;
        var img = new Rgba(pw, ph);
        Buffer.BlockCopy(raw, off, img.Px, 0, pw * ph * 4);
        return img;
    }

    private byte[] ToRawDds()
    {
        var hdr = new byte[128];
        BitConverter.GetBytes(0x20534444).CopyTo(hdr, 0);        // "DDS "
        BitConverter.GetBytes(124).CopyTo(hdr, 4);
        BitConverter.GetBytes(0x1007).CopyTo(hdr, 8);            // caps|height|width|pixelformat
        BitConverter.GetBytes(H).CopyTo(hdr, 12);
        BitConverter.GetBytes(W).CopyTo(hdr, 16);
        BitConverter.GetBytes(32).CopyTo(hdr, 76);               // pixelformat size
        BitConverter.GetBytes(0x41).CopyTo(hdr, 80);             // RGB | ALPHAPIXELS
        BitConverter.GetBytes(32).CopyTo(hdr, 88);               // bit count
        BitConverter.GetBytes(0x000000FF).CopyTo(hdr, 92);       // R
        BitConverter.GetBytes(0x0000FF00).CopyTo(hdr, 96);       // G
        BitConverter.GetBytes(0x00FF0000).CopyTo(hdr, 100);      // B
        BitConverter.GetBytes(unchecked((int)0xFF000000)).CopyTo(hdr, 104); // A
        BitConverter.GetBytes(0x1000).CopyTo(hdr, 108);          // caps: texture
        var all = new byte[128 + Px.Length];
        hdr.CopyTo(all, 0);
        Px.CopyTo(all, 128);
        return all;
    }

    /// <summary>Compress to the given DXGI format name, with a full mip chain, at width x height. srgb = pixel values already sRGB-encoded.</summary>
    public byte[] Compress(string format, int w, int h, string tmpDir, bool srgb)
    {
        Directory.CreateDirectory(tmpDir);
        string src = Path.Combine(tmpDir, "raw_" + Guid.NewGuid().ToString("N") + ".dds");
        File.WriteAllBytes(src, ToRawDds());
        string outDir = Path.Combine(tmpDir, "cmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        Run($"-ft dds -f {format} -m 0 -w {w} -h {h} {(srgb ? "-srgb " : "")}-o \"{outDir}\" \"{src}\"");
        byte[] res = File.ReadAllBytes(Directory.GetFiles(outDir, "*.dds")[0]);
        File.Delete(src);
        Directory.Delete(outDir, true);
        return res;
    }

    public byte[] Median()
    {
        var res = new byte[4];
        for (int c = 0; c < 4; c++)
        {
            var hist = new int[256];
            for (int i = c; i < Px.Length; i += 4) hist[Px[i]]++;
            int half = W * H / 2, acc = 0;
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= half) { res[c] = (byte)v; break; } }
        }
        return res;
    }
}
