using System.Numerics;
using SoulsFormats;

namespace Ac6Convert;

/// <summary>
/// Re-poses the AC6 arms from AC6's rest pose (upper arm straight down, forearm straight forward) into
/// Elden Ring's bind pose (both segments angled down and out), segment by segment, pivoting at the
/// shoulder and elbow, so that the arms follow Elden Ring's arm animation instead of fighting it.
/// </summary>
internal sealed class ArmRetarget
{
    private sealed class Side
    {
        public Vector3 Sa, Ea, Se, Ee;       // AC6 shoulder/elbow (already in ER space), ER shoulder/elbow
        public Quaternion Ru, Rf;            // upper-arm and forearm swing rotations
    }

    private readonly FLVER2 _ac;
    private readonly Dictionary<string, Side> _sides = new();   // keyed by the AC6 side prefix "L_" or "R_"
    private readonly (string side, char seg)?[] _class;

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        float d = Vector3.Dot(a, b);
        if (d < -0.9999f)
        {
            var axis = Vector3.Cross(a, Vector3.UnitX);
            if (axis.LengthSquared() < 1e-4f) axis = Vector3.Cross(a, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + d));
    }

    private static Vector3 WorldPos(FLVER2 f, Matrix4x4[] world, string name)
    {
        int i = f.Nodes.FindIndex(n => n.Name == name);
        if (i < 0) throw new InvalidOperationException($"node {name} not found");
        return world[i].Translation;
    }

    public ArmRetarget(FLVER2 ac, Func<Vector3, Vector3> toFinal, FLVER2 erBody, FLVER2 erHands)
    {
        _ac = ac;
        var acW = Skel.WorldMatrices(ac.Nodes);
        var bdW = Skel.WorldMatrices(erBody.Nodes);
        var amW = Skel.WorldMatrices(erHands.Nodes);
        foreach (var acSide in new[] { "L_", "R_" })
        {
            string erSide = acSide;   // same side: no turn needed
            var s = new Side
            {
                Sa = toFinal(WorldPos(ac, acW, acSide + "UpperArm")),
                Ea = toFinal(WorldPos(ac, acW, acSide + "ForeArm")),
                Se = WorldPos(erBody, bdW, erSide + "UpperArm"),
                Ee = WorldPos(erBody, bdW, erSide + "Forearm"),
            };
            var ha = toFinal(WorldPos(ac, acW, acSide + "Hand"));
            var he = WorldPos(erHands, amW, erSide + "Hand");
            s.Ru = FromTo(s.Ea - s.Sa, s.Ee - s.Se);
            s.Rf = FromTo(ha - s.Ea, he - s.Ee);
            _sides[acSide] = s;
            Console.WriteLine($"  arm {acSide}->{erSide}: shoulder {s.Sa}->{s.Se}, elbow {s.Ea}->{s.Ee}, upper swing {Math.Round(2 * Math.Acos(Math.Clamp(s.Ru.W, -1, 1)) * 180 / Math.PI)} deg, forearm swing {Math.Round(2 * Math.Acos(Math.Clamp(s.Rf.W, -1, 1)) * 180 / Math.PI)} deg");
        }
        // classify every AC6 node: which side, upper or forearm segment
        _class = new (string, char)?[ac.Nodes.Count];
        for (int i = 0; i < ac.Nodes.Count; i++)
        {
            int cur = i;
            while (cur >= 0 && _class[i] == null)
            {
                _class[i] = Classify(ac.Nodes[cur].Name);
                cur = ac.Nodes[cur].ParentIndex;
            }
        }
    }

    private static (string, char)? Classify(string name)
    {
        string n = name;
        foreach (var pre in new[] { "Root_", "Offset_" }) if (n.StartsWith(pre)) n = n[pre.Length..];
        string side = n.StartsWith("L_") ? "L_" : n.StartsWith("R_") ? "R_" : "";
        if (side == "") return null;
        string rest = n[2..];
        if (rest.StartsWith("Clavicle") || rest.StartsWith("UpperArm") || rest.StartsWith("UpArm") || rest.StartsWith("ArmJoint") || rest.StartsWith("Shoulder")) return (side, 'u');
        if (rest.StartsWith("ForeArm") || rest.StartsWith("Elbow") || rest.StartsWith("Wrist") || rest.StartsWith("Hand") || rest.StartsWith("Finger")) return (side, 'f');
        return null;
    }

    /// <summary>Blends the per-segment re-posing over the vertex's AC6 bone weights.</summary>
    public void Apply(FLVER.Vertex v, FLVER2.Mesh mesh, Vector3 p, Vector3 n, out Vector3 outP, out Vector3 outN)
    {
        Vector3 accP = Vector3.Zero, accN = Vector3.Zero;
        float total = 0;
        for (int k = 0; k < 4; k++)
        {
            float w = v.BoneWeights[k];
            if (w <= 0.001f) continue;
            int ab = mesh.BoneIndices.Count > 0 ? mesh.BoneIndices[v.BoneIndices[k]] : v.BoneIndices[k];
            var c = _class[ab];
            Vector3 tp = p, tn = n;
            if (c != null)
            {
                var s = _sides[c.Value.side];
                if (c.Value.seg == 'u') { tp = s.Se + Vector3.Transform(p - s.Sa, s.Ru); tn = Vector3.Transform(n, s.Ru); }
                else { tp = s.Ee + Vector3.Transform(p - s.Ea, s.Rf); tn = Vector3.Transform(n, s.Rf); }
            }
            accP += w * tp; accN += w * tn; total += w;
        }
        if (total <= 0) { outP = p; outN = n; return; }
        outP = accP / total;
        outN = accN.LengthSquared() > 1e-8f ? Vector3.Normalize(accN) : n;
    }
}
