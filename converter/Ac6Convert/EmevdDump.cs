using System.Text.Json;
using SoulsFormats;

namespace Ac6Convert;

internal static class EmevdDump
{
    // emevd <file.emevd.dcx> <er-common.emedf.json> [event id filter]
    public static int Run(string path, string emedfPath, string? only)
    {
        var names = new Dictionary<(int, int), string>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(emedfPath)))
            foreach (var cls in doc.RootElement.GetProperty("main_classes").EnumerateArray())
            {
                int bank = cls.GetProperty("index").GetInt32();
                foreach (var ins in cls.GetProperty("instrs").EnumerateArray())
                    names[(bank, ins.GetProperty("index").GetInt32())] = ins.GetProperty("name").GetString()!;
            }
        var ev = EMEVD.Read(path);
        Console.WriteLine($"{path}: {ev.Events.Count} events");
        foreach (var e in ev.Events)
        {
            if (only != null && e.ID.ToString() != only) continue;
            Console.WriteLine($"Event {e.ID} rest={e.RestBehavior} instrs={e.Instructions.Count}");
            foreach (var i in e.Instructions)
            {
                string n = names.TryGetValue((i.Bank, i.ID), out var s) ? s : "?";
                var words = new List<string>();
                var d = i.ArgData;
                for (int k = 0; k + 4 <= d.Length; k += 4) words.Add(BitConverter.ToInt32(d, k).ToString());
                Console.WriteLine($"   {i.Bank}[{i.ID}] {n}  ({string.Join(", ", words)})");
            }
        }
        return 0;
    }
}
