using SoulsFormats;

namespace Ac6Convert;

internal static class ParamDump
{
    // paramdump <X.param> <X paramdef xml> [row id]   prints every row (or one row) as name=value lines
    public static int Run(string paramPath, string defPath, string? rowFilter)
    {
        var def = PARAMDEF.XmlDeserialize(defPath);
        var p = PARAM.Read(paramPath);
        p.ApplyParamdef(def);
        foreach (var row in p.Rows)
        {
            if (rowFilter != null && row.ID.ToString() != rowFilter) continue;
            Console.WriteLine($"-- row {row.ID} {row.Name}");
            foreach (var c in row.Cells)
                Console.WriteLine($"   {c.Def.InternalName} = {c.Value}");
        }
        return 0;
    }
}
