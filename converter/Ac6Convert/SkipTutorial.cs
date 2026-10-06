using SoulsFormats;

namespace Ac6Convert;

/// <summary>
/// Patches the Chapel of Anticipation's event script so a new character is warped straight to the Limgrave
/// arrival point instead of running the tutorial. The patched file is written from the player's own copy.
/// </summary>
internal static class SkipTutorial
{
    // The arrival spot the game itself uses after the tutorial (map m60_42_36, region 1042362980),
    // and the new event we add.
    private const uint ArrivalRegion = 1042362980;
    private const long NewEventId = 10009999;

    public static int Run(string srcPath, string outPath)
    {
        var emevd = EMEVD.Read(srcPath);
        if (emevd.Events.Any(e => e.ID == NewEventId)) { Console.WriteLine("already patched"); return 0; }

        static byte[] Words(params int[] w) { var b = new byte[w.Length * 4]; for (int i = 0; i < w.Length; i++) BitConverter.GetBytes(w[i]).CopyTo(b, i * 4); return b; }

        var ev = new EMEVD.Event(NewEventId, EMEVD.Event.RestBehaviorType.Default);
        ev.Instructions.Add(new EMEVD.Instruction(1001, 6, Words(30)));                       // wait 30 frames so the map is up
        ev.Instructions.Add(new EMEVD.Instruction(2003, 23, Words((int)ArrivalRegion)));      // respawn point = Limgrave arrival
        // Warp Player: area 60, block 42, region 36, index 0 -> packed into one word, then the arrival region, then no popup
        int packed = 60 | (42 << 8) | (36 << 16) | (0 << 24);
        ev.Instructions.Add(new EMEVD.Instruction(2003, 14, Words(packed, (int)ArrivalRegion, 0)));
        emevd.Events.Add(ev);

        // start it from the map's init event
        var ev0 = emevd.Events.First(e => e.ID == 0);
        ev0.Instructions.Insert(0, new EMEVD.Instruction(2000, 0, Words(0, (int)NewEventId, 0)));

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        emevd.Write(outPath);
        Console.WriteLine($"wrote {outPath}: added event {NewEventId} (wait, set respawn, warp to m60_42_36) and started it from event 0");
        return 0;
    }
}
