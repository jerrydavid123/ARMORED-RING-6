using System.Numerics;
using Havoc;
using Havoc.IO.Tagfile.Binary;
using Havoc.Objects;
using Havoc.Reflection;
using SoulsAssetPipeline.Animation;
using SoulsAssetPipeline.AnimationImporting;
using SoulsFormats;

// Ports one Armored Core VI animation onto the Elden Ring player skeleton.
//
//   AC6 clip (hkaSplineCompressedAnimation, Havok 2019 tagfile + compendium)
//     -> decoded to per-frame local bone transforms (SoulsAssetPipeline's spline decoder)
//     -> retargeted: the world-space rotation delta of every matched bone (AC6 pose vs AC6 rest) is applied on top of the
//        Elden Ring rest pose; unmatched Elden Ring bones keep their rest pose
//     -> re-compressed with Havok's own spline compressor (CompressAnim.exe from SoulsAssetPipeline, 2010 format)
//     -> the compressed block is injected into a copy of an Elden Ring animation file (Havok 2018 tagfile), so every
//        other field the engine expects (binding, skeleton name, types) stays exactly as Elden Ring wrote it.
internal static class AnimPort
{
    public class Skel
    {
        public string[] Names;
        public int[] Parent;
        public Vector3[] T;
        public Quaternion[] Q;
        public Vector3[] S;
        public int Count => Names.Length;
    }

    public static IHkObject Field(IHkObject o, string name) => (o as HkClass)?.Value.FirstOrDefault(kv => kv.Key.Name == name).Value;
    public static IHkObject Deref(IHkObject o) => o is HkPtr p ? p.Value : o;
    static float[] F(IHkObject o) => ((HkArray)o).Value.Select(x => Convert.ToSingle(x.Value)).ToArray();

    public static Skel LoadSkel(string path)
    {
        var root = HkBinaryTagfileReader.Read(File.ReadAllBytes(path), null);
        var cont = Deref(Field(((HkArray)Field(root, "namedVariants")).Value[0], "variant"));
        var sk = Deref(((HkArray)Field(cont, "skeletons")).Value[0]);
        var bones = (HkArray)Field(sk, "bones");
        var pose = (HkArray)Field(sk, "referencePose");
        var s = new Skel
        {
            Names = bones.Value.Select(b => Convert.ToString(Field(b, "name").Value)).ToArray(),
            Parent = ((HkArray)Field(sk, "parentIndices")).Value.Select(x => Convert.ToInt32(x.Value)).ToArray(),
        };
        s.T = new Vector3[s.Count]; s.Q = new Quaternion[s.Count]; s.S = new Vector3[s.Count];
        for (int i = 0; i < s.Count; i++)
        {
            var t = F(Field(pose.Value[i], "translation")); var q = F(Field(pose.Value[i], "rotation")); var sc = F(Field(pose.Value[i], "scale"));
            s.T[i] = new Vector3(t[0], t[1], t[2]);
            s.Q[i] = Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3]));
            s.S[i] = new Vector3(sc[0], sc[1], sc[2]);
        }
        return s;
    }

    // ---- bone mapping AC6 -> Elden Ring ----
    static readonly System.Text.RegularExpressions.Regex Exclude = new(
        "Twist|Elbow|Knee|EX|Link|Offset|Dummy|Skirt|Mantle|Armor|Target|Weapon|Xtra|ArmJoint|Wrist|Rot_XYZ|FreeAnim|BD_Root|Shield|Pectoral|Collar|Axilla|Shoulder|Hip|Coxa|Thigh_|Calf_",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static Dictionary<int, int> BuildMap(Skel ac, Skel er)
    {
        string Norm(string n) => n.Replace("_", "").ToLowerInvariant();
        var erIndex = new Dictionary<string, int>();
        for (int i = 0; i < er.Count; i++) erIndex[Norm(er.Names[i])] = i;
        var over = new Dictionary<string, string>
        {
            ["Root"] = "RootPos", ["SpineLink"] = "Spine1", ["Spine1"] = "Spine2",
            ["L_ForeArm"] = "L_Forearm", ["R_ForeArm"] = "R_Forearm", ["L_Toe"] = "L_Toe0", ["R_Toe"] = "R_Toe0",
        };
        var map = new Dictionary<int, int>(); // ac6 bone -> er bone
        var used = new HashSet<int>();
        for (int a = 0; a < ac.Count; a++)
        {
            string name = ac.Names[a];
            string target = null;
            if (over.TryGetValue(name, out var o)) target = o;
            else if (!Exclude.IsMatch(name)) target = name;
            if (target == null) continue;
            if (!erIndex.TryGetValue(Norm(target), out int e)) continue;
            if (used.Contains(e)) continue;
            map[a] = e; used.Add(e);
        }
        return map;
    }

    static Quaternion[] WorldRot(Skel s, Quaternion[] local)
    {
        var w = new Quaternion[s.Count];
        for (int i = 0; i < s.Count; i++)
            w[i] = s.Parent[i] < 0 ? local[i] : Quaternion.Normalize(w[s.Parent[i]] * local[i]);
        return w;
    }

    public class Result
    {
        public int Frames;
        public float FrameDuration;
        public List<NewBlendableTransform[]> Pose = new(); // per frame, per ER bone (all bones)
        public int Mapped;
    }

    // ---- retarget v2: limbs by world direction, torso/head by world rotation delta ----
    // AC6's mesh arms were re-posed into Elden Ring's A-pose when the kit was converted, and the two skeletons' rest poses
    // differ (AC6 arms hang straight down, Elden Ring's sit 45 degrees out), so a rotation delta from each skeleton's own rest
    // pose lands 45 degrees off. Matching where each bone points in the world does not care about the rest pose.
    static readonly (string ac, string acChild, string er, string erChild)[] DirPairs = BuildDirPairs();

    static (string, string, string, string)[] BuildDirPairs()
    {
        var l = new List<(string, string, string, string)>
        {
            ("Spine", "SpineLink", "Spine", "Spine1"),
            ("SpineLink", "Spine1", "Spine1", "Spine2"),
            ("Spine1", "Neck", "Spine2", "Neck"),
            ("Neck", "Head", "Neck", "Head"),
        };
        foreach (var s in new[] { "L", "R" })
        {
            l.Add(($"{s}_Clavicle", $"{s}_UpperArm", $"{s}_Clavicle", $"{s}_UpperArm"));
            l.Add(($"{s}_UpperArm", $"{s}_ForeArm", $"{s}_UpperArm", $"{s}_Forearm"));
            l.Add(($"{s}_ForeArm", $"{s}_Hand", $"{s}_Forearm", $"{s}_Hand"));
            l.Add(($"{s}_Thigh", $"{s}_Calf", $"{s}_Thigh", $"{s}_Calf"));
            l.Add(($"{s}_Calf", $"{s}_Foot", $"{s}_Calf", $"{s}_Foot"));
            l.Add(($"{s}_Foot", $"{s}_Toe", $"{s}_Foot", $"{s}_Toe0"));
        }
        return l.ToArray();
    }

    static readonly (string ac, string er)[] DeltaPairs = { ("Pelvis", "Pelvis"), ("Head", "Head") };

    static Quaternion FromTo(Vector3 u, Vector3 v)
    {
        u = Vector3.Normalize(u); v = Vector3.Normalize(v);
        float d = Vector3.Dot(u, v);
        if (d < -0.999999f)
        {
            var axis = Vector3.Cross(u, Math.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(u, v);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + d));
    }

    internal static void Fk(Skel s, Quaternion[] lq, Vector3[] lt, out Quaternion[] wq, out Vector3[] wp)
    {
        wq = new Quaternion[s.Count]; wp = new Vector3[s.Count];
        for (int i = 0; i < s.Count; i++)
        {
            int p = s.Parent[i];
            if (p < 0) { wq[i] = lq[i]; wp[i] = lt[i]; }
            else
            {
                wq[i] = Quaternion.Normalize(wq[p] * lq[i]);
                wp[i] = wp[p] + Vector3.Transform(lt[i], wq[p]);
            }
        }
    }

    public static Result Retarget(HavokAnimationData_SplineCompressed data, int frameCount, float frameDuration, Skel ac, Skel er, float posScale, bool translateRoot)
    {
        int Find(Skel s, string n) { for (int i = 0; i < s.Count; i++) if (s.Names[i] == n) return i; return -1; }
        var dirs = new Dictionary<int, (int ac, int acChild, int erChild)>(); // er bone -> ...
        foreach (var (a, ach, e, ech) in DirPairs)
        {
            int ia = Find(ac, a), iac = Find(ac, ach), ie = Find(er, e), iec = Find(er, ech);
            if (ia >= 0 && iac >= 0 && ie >= 0 && iec >= 0) dirs[ie] = (ia, iac, iec);
        }
        var deltas = new Dictionary<int, int>();
        foreach (var (a, e) in DeltaPairs)
        {
            int ia = Find(ac, a), ie = Find(er, e);
            if (ia >= 0 && ie >= 0) deltas[ie] = ia;
        }
        int rootAc = Find(ac, "Root"), rootEr = Find(er, "RootPos");
        var res = new Result { Frames = frameCount, FrameDuration = frameDuration, Mapped = dirs.Count + deltas.Count };

        Fk(ac, ac.Q, ac.T, out var acRestW, out _);
        Fk(er, er.Q, er.T, out var erRestW, out var erRestP);
        var erRestDir = new Dictionary<int, Vector3>();
        foreach (var kv in dirs) erRestDir[kv.Key] = Vector3.Normalize(erRestP[kv.Value.erChild] - erRestP[kv.Key]);

        for (int f = 0; f < frameCount; f++)
        {
            var aLocalQ = new Quaternion[ac.Count]; var aLocalT = new Vector3[ac.Count];
            for (int b = 0; b < ac.Count; b++)
            {
                var tr = data.GetTransformOnFrameByBone(b, f, false);
                aLocalQ[b] = Quaternion.Normalize(tr.Rotation); aLocalT[b] = tr.Translation;
            }
            Fk(ac, aLocalQ, aLocalT, out var aW, out var aP);

            var eLocalQ = new Quaternion[er.Count]; var eLocalT = new Vector3[er.Count]; var eW = new Quaternion[er.Count];
            for (int e = 0; e < er.Count; e++)
            {
                int p = er.Parent[e];
                Quaternion parentW = p >= 0 ? eW[p] : Quaternion.Identity;
                Quaternion desiredW = Quaternion.Normalize(parentW * er.Q[e]);
                if (dirs.TryGetValue(e, out var d))
                {
                    var want = aP[d.acChild] - aP[d.ac];
                    if (want.LengthSquared() > 1e-12f)
                        desiredW = Quaternion.Normalize(FromTo(erRestDir[e], want) * erRestW[e]);
                }
                else if (deltas.TryGetValue(e, out int a))
                {
                    var delta = Quaternion.Normalize(aW[a] * Quaternion.Inverse(acRestW[a]));
                    desiredW = Quaternion.Normalize(delta * erRestW[e]);
                }
                eW[e] = desiredW;
                eLocalQ[e] = Quaternion.Normalize(Quaternion.Inverse(parentW) * desiredW);
                eLocalT[e] = er.T[e];
                if (translateRoot && e == rootEr && rootAc >= 0)
                {
                    Quaternion apW = ac.Parent[rootAc] >= 0 ? aW[ac.Parent[rootAc]] : Quaternion.Identity;
                    var dWorld = Vector3.Transform(aLocalT[rootAc] - ac.T[rootAc], apW);
                    var dLocal = Vector3.Transform(dWorld * posScale, Quaternion.Inverse(parentW));
                    eLocalT[e] = er.T[e] + dLocal;
                }
            }
            var frame = new NewBlendableTransform[er.Count];
            for (int e = 0; e < er.Count; e++) frame[e] = new NewBlendableTransform(eLocalT[e], er.S[e], eLocalQ[e]);
            res.Pose.Add(frame);
        }
        return res;
    }

    // ---- compression through the Havok compressor shipped with SoulsAssetPipeline ----
    public static HKX Compress2010(Result r, Skel er, int[] trackToBone, string sapDir)
    {
        var imp = new ImportedAnimation();
        imp.Duration = (r.Frames - 1) * r.FrameDuration;
        imp.FrameDuration = r.FrameDuration;
        imp.FrameCount = r.Frames;
        // one track per entry of the template's binding: track t animates Elden Ring bone trackToBone[t]
        for (int t = 0; t < trackToBone.Length; t++)
        {
            string name = er.Names[trackToBone[t]];
            imp.TransformTrackNames.Add(name);
            imp.TransformTrackToBoneIndices[name] = trackToBone[t];
        }
        foreach (var pose in r.Pose)
        {
            var fr = new ImportedAnimation.Frame();
            for (int t = 0; t < trackToBone.Length; t++) fr.BoneTransforms.Add(pose[trackToBone[t]]);
            imp.Frames.Add(fr);
        }
        byte[] bytes = imp.WriteToSplineCompressedHKX2010Bytes(SplineCompressedAnimation.RotationQuantizationType.THREECOMP40, 0.001f, sapDir);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "anim_port_2010.hkx"), bytes);
        return HKX.Read(bytes);
    }

    // ---- injection into an Elden Ring template tagfile ----
    static IHkObject Num(IHkObject like, double v) => like switch
    {
        HkByte => new HkByte(like.Type, (byte)v),
        HkSByte => new HkSByte(like.Type, (sbyte)v),
        HkInt16 => new HkInt16(like.Type, (short)v),
        HkUInt16 => new HkUInt16(like.Type, (ushort)v),
        HkInt32 => new HkInt32(like.Type, (int)v),
        HkUInt32 => new HkUInt32(like.Type, (uint)v),
        HkInt64 => new HkInt64(like.Type, (long)v),
        HkUInt64 => new HkUInt64(like.Type, (ulong)v),
        HkSingle => new HkSingle(like.Type, (float)v),
        HkDouble => new HkDouble(like.Type, v),
        _ => throw new InvalidOperationException("not a number object: " + like.GetType().Name),
    };

    static IHkObject NumArray(IHkObject templateArray, IEnumerable<double> values)
    {
        var a = (HkArray)templateArray;
        var like = a.Value.FirstOrDefault();
        if (like == null) throw new InvalidOperationException("template array is empty, cannot infer element type");
        return new HkArray(a.Type, values.Select(v => Num(like, v)).ToList());
    }

    static IHkObject WithFields(IHkObject cls, Dictionary<string, IHkObject> repl)
    {
        var c = (HkClass)cls;
        var d = new Dictionary<HkField, IHkObject>();
        foreach (var kv in c.Value) d[kv.Key] = repl.TryGetValue(kv.Key.Name, out var v) ? v : kv.Value;
        return new HkClass(c.Type, d);
    }

    public static IHkObject Inject(IHkObject templateRoot, HKX compressed2010, int trackCount, int[] trackToBone, Result r)
    {
        HKX.HKASplineCompressedAnimation a = null;
        foreach (var o in compressed2010.DataSection.Objects) if (o is HKX.HKASplineCompressedAnimation x) a = x;
        if (a == null) throw new InvalidOperationException("compressor output has no spline animation");

        var nv = (HkClass)((HkArray)Field(templateRoot, "namedVariants")).Value[0];
        var cont = Deref(Field(nv, "variant"));
        var anim = Deref(((HkArray)Field(cont, "animations")).Value[0]);
        var bind = Deref(((HkArray)Field(cont, "bindings")).Value[0]);

        var data = a.Data.GetArrayData().Elements.Select(e => (double)e.data);
        var newAnim = WithFields(anim, new Dictionary<string, IHkObject>
        {
            ["duration"] = Num(Field(anim, "duration"), a.Duration),
            ["numberOfTransformTracks"] = Num(Field(anim, "numberOfTransformTracks"), trackCount),
            ["numFrames"] = Num(Field(anim, "numFrames"), a.FrameCount),
            ["numBlocks"] = Num(Field(anim, "numBlocks"), a.BlockCount),
            ["maxFramesPerBlock"] = Num(Field(anim, "maxFramesPerBlock"), a.FramesPerBlock),
            ["maskAndQuantizationSize"] = Num(Field(anim, "maskAndQuantizationSize"), a.MaskAndQuantization),
            ["blockDuration"] = Num(Field(anim, "blockDuration"), a.BlockDuration),
            ["blockInverseDuration"] = Num(Field(anim, "blockInverseDuration"), a.InverseBlockDuration),
            ["frameDuration"] = Num(Field(anim, "frameDuration"), a.FrameDuration),
            ["blockOffsets"] = NumArray(Field(anim, "blockOffsets"), a.BlockOffsets.GetArrayData().Elements.Select(e => (double)e.data)),
            ["floatBlockOffsets"] = NumArray(Field(anim, "floatBlockOffsets"), a.FloatBlockOffsets.GetArrayData().Elements.Select(e => (double)e.data)),
            ["data"] = NumArray(Field(anim, "data"), data),
            ["extractedMotion"] = new HkPtr(Field(anim, "extractedMotion").Type, null),
        });
        var newBind = WithFields(bind, new Dictionary<string, IHkObject>
        {
            ["animation"] = new HkPtr(Field(bind, "animation").Type, newAnim),
            ["transformTrackToBoneIndices"] = NumArray(Field(bind, "transformTrackToBoneIndices"), trackToBone.Select(i => (double)i)),
        });
        var newCont = WithFields(cont, new Dictionary<string, IHkObject>
        {
            ["animations"] = new HkArray(((HkArray)Field(cont, "animations")).Type, new List<IHkObject> { new HkPtr(((HkArray)Field(cont, "animations")).Value[0].Type, newAnim) }),
            ["bindings"] = new HkArray(((HkArray)Field(cont, "bindings")).Type, new List<IHkObject> { new HkPtr(((HkArray)Field(cont, "bindings")).Value[0].Type, newBind) }),
        });
        var newNv = WithFields(nv, new Dictionary<string, IHkObject> { ["variant"] = new HkPtr(Field(nv, "variant").Type, newCont) });
        return WithFields(templateRoot, new Dictionary<string, IHkObject>
        {
            ["namedVariants"] = new HkArray(((HkArray)Field(templateRoot, "namedVariants")).Type, new List<IHkObject> { newNv }),
        });
    }

    // ---- command ----
    public static int Run(string[] a)
    {
        // port <ac6.hkx> <ac6.compendium> <ac6 skeleton.hkx> <er skeleton.hkx> <er template.hkx> <er template.compendium> <out.hkx> <sapDir> [posScale]
        string ac6Hkx = a[1], ac6Comp = a[2], ac6SkelPath = a[3], erSkelPath = a[4], erTplPath = a[5], erTplComp = a[6], outPath = a[7], sapDir = a[8];
        float posScale = a.Length > 9 ? float.Parse(a[9], System.Globalization.CultureInfo.InvariantCulture) : 0.1459f;

        var ac = LoadSkel(ac6SkelPath);
        var er = LoadSkel(erSkelPath);
        Console.WriteLine($"AC6 bones {ac.Count}, ER bones {er.Count}");

        bool fit = a.Skip(10).Contains("--fit");
        bool zeroRoot = a.Skip(10).Contains("--noroot");
        // decode the AC6 clip(s); several files separated by ';' are played one after the other
        var fakeSkel = HKX.GenFakeFromTagFile(File.ReadAllBytes(ac6SkelPath), null);
        HKX.HKASkeleton hkSkel = null;
        foreach (var o in fakeSkel.DataSection.Objects) if (o is HKX.HKASkeleton s) hkSkel = s;
        Result res = null;
        float firstFd = 0;
        foreach (var clipPath in ac6Hkx.Split(';'))
        {
            var fakeAnim = HKX.GenFakeFromTagFile(File.ReadAllBytes(clipPath), File.ReadAllBytes(ac6Comp));
            HKX.HKAAnimationBinding hkBind = null; HKX.HKASplineCompressedAnimation hkAnim = null; HKX.HKADefaultAnimatedReferenceFrame hkRef = null;
            foreach (var o in fakeAnim.DataSection.Objects)
            {
                if (o is HKX.HKAAnimationBinding b) hkBind = b;
                else if (o is HKX.HKASplineCompressedAnimation sa) hkAnim = sa;
                else if (o is HKX.HKADefaultAnimatedReferenceFrame rf) hkRef = rf;
            }
            if (hkBind.BlendHint != HKX.AnimationBlendHint.NORMAL)
            {
                // additive clips are offsets from a pose, not poses: keep the Elden Ring clip
                Console.WriteLine($"SKIPPED additive clip {Path.GetFileName(clipPath)}");
                return 3;
            }
            var data = new HavokAnimationData_SplineCompressed(0, "ac6", hkSkel, hkRef, hkBind, hkAnim);
            Console.WriteLine($"AC6 clip {Path.GetFileName(clipPath)}: {hkAnim.FrameCount} frames, {hkAnim.Duration:0.00} s, {hkAnim.TransformTrackCount} tracks");
            var part = Retarget(data, hkAnim.FrameCount, hkAnim.FrameDuration, ac, er, posScale, translateRoot: true);
            if (res == null) { res = part; firstFd = hkAnim.FrameDuration; }
            else { res.Pose.AddRange(part.Pose); res.Frames += part.Frames; }
        }
        Console.WriteLine($"retargeted {res.Mapped} bones over {res.Frames} frames");

        // template (for track count and binding)
        var tpl = HkBinaryTagfileReader.Read(File.ReadAllBytes(erTplPath), File.ReadAllBytes(erTplComp));
        var tplBind = Deref(((HkArray)Field(Deref(Field(((HkArray)Field(tpl, "namedVariants")).Value[0], "variant")), "bindings")).Value[0]);
        var trackToBone = ((HkArray)Field(tplBind, "transformTrackToBoneIndices")).Value.Select(x => Convert.ToInt32(x.Value)).ToArray();
        Console.WriteLine($"ER template: {trackToBone.Length} tracks");

        // long idle-style loops (10 s) keep their own speed; everything else follows the Elden Ring clip's timing
        if (fit && (res.Frames - 1) * res.FrameDuration < 9.9f)
        {
            float target = TemplateDuration(File.ReadAllBytes(erTplPath));
            res.FrameDuration = target / Math.Max(1, res.Frames - 1);
            Console.WriteLine($"time-fitted to the Elden Ring clip length {target:0.00} s");
        }
        var comp = Compress2010(res, er, trackToBone, sapDir);
        byte[] patched = PatchTemplate(File.ReadAllBytes(erTplPath), comp, zeroRoot);
        File.WriteAllBytes(outPath, patched);
        Console.WriteLine($"wrote {outPath} ({patched.Length} bytes, template {new FileInfo(erTplPath).Length})");
        return 0;
    }

    /// <summary>Duration in seconds of the spline animation inside an Elden Ring clip file.</summary>
    public static float TemplateDuration(byte[] template)
    {
        var tp = TagPatch.Load(template);
        for (int i = 1; i < tp.Items.Count; i++)
        {
            var it = tp.Items[i];
            if (it.Count != 1) continue;
            int off = (int)it.Offset;
            if (off + 184 > tp.Data.Length) continue;
            if (BitConverter.ToUInt32(tp.Data, off + 24) == 3 && BitConverter.ToSingle(tp.Data, off + 28) > 0) return BitConverter.ToSingle(tp.Data, off + 28);
        }
        throw new InvalidOperationException("no spline animation record found in the template");
    }

    /// <summary>Puts the compressed block of <paramref name="comp"/> into a byte-level copy of an Elden Ring animation file.</summary>
    public static byte[] PatchTemplate(byte[] template, HKX comp, bool zeroRoot = false)
    {
        HKX.HKASplineCompressedAnimation a = null;
        foreach (var o in comp.DataSection.Objects) if (o is HKX.HKASplineCompressedAnimation x) a = x;
        if (a == null) throw new InvalidOperationException("compressor output has no spline animation");

        var tp = TagPatch.Load(template);
        // the animation record: 184 bytes, animation type 3 (spline compressed) at +24
        int animIdx = -1;
        for (int i = 1; i < tp.Items.Count; i++)
        {
            var it = tp.Items[i];
            if (it.Count != 1) continue;
            int off = (int)it.Offset;
            if (off + 184 > tp.Data.Length) continue;
            if (BitConverter.ToUInt32(tp.Data, off + 24) == 3 && BitConverter.ToSingle(tp.Data, off + 28) > 0) { animIdx = i; break; }
        }
        if (animIdx < 0) throw new InvalidOperationException("no spline animation record found in the template");
        int ao = (int)tp.Items[animIdx].Offset;
        int blockOffsetsIdx = (int)BitConverter.ToUInt32(tp.Data, ao + 96);
        int floatBlockIdx = (int)BitConverter.ToUInt32(tp.Data, ao + 112);
        int dataIdx = (int)BitConverter.ToUInt32(tp.Data, ao + 160);

        tp.SetF32(ao + 28, a.Duration);
        tp.SetU32(ao + 64, (uint)a.FrameCount);
        tp.SetU32(ao + 68, (uint)a.BlockCount);
        tp.SetU32(ao + 72, (uint)a.FramesPerBlock);
        tp.SetU32(ao + 76, a.MaskAndQuantization);
        tp.SetF32(ao + 80, a.BlockDuration);
        tp.SetF32(ao + 84, a.InverseBlockDuration);
        tp.SetF32(ao + 88, a.FrameDuration);

        byte[] U32s(IEnumerable<uint> v) => v.SelectMany(BitConverter.GetBytes).ToArray();
        var bo = a.BlockOffsets.GetArrayData().Elements.Select(e => (uint)e.data).ToList();
        var fbo = a.FloatBlockOffsets.GetArrayData().Elements.Select(e => (uint)e.data).ToList();
        var data = a.Data.GetArrayData().Elements.Select(e => (byte)e.data).ToArray();
        var repl = new Dictionary<int, (byte[], uint)>
        {
            [blockOffsetsIdx] = (U32s(bo), (uint)bo.Count),
            [floatBlockIdx] = (U32s(fbo), (uint)fbo.Count),
            [dataIdx] = (data, (uint)data.Length),
        };

        // root motion: keep Elden Ring's own walk/run speed (the game moves the character by it), stretched to the new clip length
        uint refIdx = BitConverter.ToUInt32(tp.Data, ao + 40);
        if (refIdx != 0)
        {
            var rec = tp.Items[(int)refIdx];
            int ro = (int)rec.Offset;
            // the samples array is the item a pointer patch inside the record points at
            int samplesIdx = -1;
            foreach (var (type, offs) in tp.Patches)
                foreach (var po in offs)
                    if (po >= ro && po < ro + 96 && BitConverter.ToUInt32(tp.Data, (int)po) != 0 && BitConverter.ToUInt32(tp.Data, (int)po) < tp.Items.Count
                        && tp.Items[(int)BitConverter.ToUInt32(tp.Data, (int)po)].Count > 1 && tp.Items[(int)BitConverter.ToUInt32(tp.Data, (int)po)].Offset != tp.Items[animIdx].Offset)
                        samplesIdx = (int)BitConverter.ToUInt32(tp.Data, (int)po);
            if (samplesIdx > 0)
            {
                var si = tp.Items[samplesIdx];
                int n = (int)si.Count;
                var src = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    src[i] = new float[4];
                    for (int c = 0; c < 4; c++) src[i][c] = BitConverter.ToSingle(tp.Data, (int)si.Offset + i * 16 + c * 4);
                }
                float oldDur = 0;
                // the record stores its duration as a float; find it by matching the template animation's old duration
                float tplDur = BitConverter.ToSingle(tp.Data, ao + 28 - 0); // already overwritten above, so recompute from sample count below
                oldDur = (n - 1) / 30f;
                int m = a.FrameCount;
                float newDur = a.Duration;
                var outS = new List<byte>();
                for (int j = 0; j < m; j++)
                {
                    float t = j * newDur / Math.Max(1, m - 1);
                    float u = t / oldDur * (n - 1); // position in source sample units, may exceed n-1 (extrapolated at the average speed)
                    var v = new float[4];
                    for (int c = 0; c < 4; c++)
                    {
                        float endV = (src[n - 1][c] - src[0][c]) / (n - 1);
                        if (u <= n - 1)
                        {
                            int i0 = (int)Math.Floor(u); int i1 = Math.Min(n - 1, i0 + 1); float f = u - i0;
                            v[c] = src[i0][c] * (1 - f) + src[i1][c] * f;
                        }
                        else v[c] = src[n - 1][c] + (u - (n - 1)) * endV;
                    }
                    if (zeroRoot) { v[0] = 0; v[1] = 0; v[2] = 0; } // attack clips: the mod moves the mech, the animation must not
                    for (int c = 0; c < 4; c++) outS.AddRange(BitConverter.GetBytes(v[c]));
                }
                repl[samplesIdx] = (outS.ToArray(), (uint)m);
                // the reference frame's own duration field
                for (int o = ro; o + 4 <= ro + 96; o += 4)
                    if (Math.Abs(BitConverter.ToSingle(tp.Data, o) - oldDur) < 1e-3f) { tp.SetF32(o, newDur); break; }
            }
        }
        tp.Replace(repl);
        return tp.Save();
    }
}
