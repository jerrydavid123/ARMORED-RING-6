using SoulsAssetPipeline.Animation;

// HkxTool bones <clip.hkx> <compendium> <ac6 skeleton.hkx>: the skeleton bones a (partial) clip animates
internal static class ClipBones
{
    public static int Run(string[] a)
    {
        var skel = AnimPort.LoadSkel(a[3]);
        var fake = HKX.GenFakeFromTagFile(File.ReadAllBytes(a[1]), File.ReadAllBytes(a[2]));
        foreach (var o in fake.DataSection.Objects)
            if (o is HKX.HKAAnimationBinding b)
            {
                var idx = b.TransformTrackToBoneIndices.GetArrayData().Elements.Select(e => (int)Convert.ToInt32(e.data)).ToList();
                Console.WriteLine($"{Path.GetFileName(a[1])}: blend {b.BlendHint}, {idx.Count} tracks: " + string.Join(", ", idx.Select(i => i >= 0 && i < skel.Count ? skel.Names[i] : "?" + i)));
            }
        return 0;
    }
}
