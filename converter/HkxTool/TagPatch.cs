using System.Text;

// In-place editing of a Havok tagfile (TAG0) that keeps the layout Elden Ring wrote: DATA holds the items back to back,
// TCRF names the shared type compendium, INDX has ITEM (12-byte entries: type|flags, offset, count) and PTCH (pointer
// patch offsets per type). Chunk headers are big-endian (size + flags in the top two bits), contents little-endian.
// Only what the animation port needs is supported: replacing the payload of items (arrays), poking scalar fields, and
// re-laying out the items that follow a resized one.
internal class TagPatch
{
    public class Chunk { public string Tag; public int Flags; public byte[] Body; public List<Chunk> Kids; }
    public class Item { public uint Word0; public uint Offset; public uint Count; public int Align; }

    public Chunk Root;
    public byte[] Data;                    // DATA payload
    public List<Item> Items = new();
    public List<(uint type, List<uint> offsets)> Patches = new();
    Chunk dataChunk, itemChunk, patchChunk;

    public static TagPatch Load(byte[] file)
    {
        var t = new TagPatch();
        t.Root = ReadChunk(file, 0, out _);
        foreach (var k in t.Root.Kids)
        {
            if (k.Tag == "DATA") { t.dataChunk = k; t.Data = (byte[])k.Body.Clone(); }
            if (k.Tag == "INDX")
                foreach (var ik in k.Kids)
                {
                    if (ik.Tag == "ITEM") t.itemChunk = ik;
                    if (ik.Tag == "PTCH") t.patchChunk = ik;
                }
        }
        var ib = t.itemChunk.Body;
        for (int p = 0; p + 12 <= ib.Length; p += 12)
            t.Items.Add(new Item { Word0 = BitConverter.ToUInt32(ib, p), Offset = BitConverter.ToUInt32(ib, p + 4), Count = BitConverter.ToUInt32(ib, p + 8) });
        foreach (var it in t.Items)
        {
            int a = 1;
            while (a < 16 && it.Offset % (a * 2) == 0) a *= 2;
            it.Align = a;
        }
        var pb = t.patchChunk.Body;
        for (int p = 0; p + 8 <= pb.Length;)
        {
            uint type = BitConverter.ToUInt32(pb, p); uint cnt = BitConverter.ToUInt32(pb, p + 4); p += 8;
            var offs = new List<uint>();
            for (int i = 0; i < cnt; i++, p += 4) offs.Add(BitConverter.ToUInt32(pb, p));
            t.Patches.Add((type, offs));
        }
        return t;
    }

    static Chunk ReadChunk(byte[] b, int off, out int size)
    {
        uint raw = (uint)(b[off] << 24 | b[off + 1] << 16 | b[off + 2] << 8 | b[off + 3]);
        size = (int)(raw & 0x3FFFFFFF);
        var c = new Chunk { Flags = (int)(raw >> 30), Tag = Encoding.ASCII.GetString(b, off + 4, 4) };
        int bodyStart = off + 8, end = off + size;
        if (c.Tag is "TAG0" or "INDX")
        {
            c.Kids = new();
            for (int p = bodyStart; p < end;)
            {
                c.Kids.Add(ReadChunk(b, p, out int ks));
                p += ks;
            }
            c.Body = Array.Empty<byte>();
        }
        else c.Body = b[bodyStart..end];
        return c;
    }

    static void WriteChunk(Chunk c, List<byte> o)
    {
        List<byte> body = new();
        if (c.Kids != null) foreach (var k in c.Kids) WriteChunk(k, body);
        else body.AddRange(c.Body);
        uint size = (uint)(8 + body.Count);
        uint raw = size | ((uint)c.Flags << 30);
        o.Add((byte)(raw >> 24)); o.Add((byte)(raw >> 16)); o.Add((byte)(raw >> 8)); o.Add((byte)raw);
        o.AddRange(Encoding.ASCII.GetBytes(c.Tag));
        o.AddRange(body);
    }

    // payload size in bytes of item i as stored (span up to the next item by offset, or to the end of DATA)
    int Span(int i)
    {
        var order = Enumerable.Range(0, Items.Count).Where(k => Items[k].Offset > 0 || k == 1).OrderBy(k => Items[k].Offset).ToList();
        int pos = order.IndexOf(i);
        uint next = pos + 1 < order.Count ? Items[order[pos + 1]].Offset : (uint)Data.Length;
        return (int)(next - Items[i].Offset);
    }

    public void SetF32(int dataOffset, float v) => BitConverter.GetBytes(v).CopyTo(Data, dataOffset);
    public void SetU32(int dataOffset, uint v) => BitConverter.GetBytes(v).CopyTo(Data, dataOffset);

    /// <summary>Replace the payload (and element count) of items, then re-lay out every item after the first changed one.</summary>
    public void Replace(Dictionary<int, (byte[] payload, uint count)> repl)
    {
        // original payloads by item
        var order = Enumerable.Range(0, Items.Count).Where(k => k == 1 || Items[k].Offset > 0).OrderBy(k => Items[k].Offset).ToList();
        var spans = order.ToDictionary(k => k, k => Span(k));
        var payload = new Dictionary<int, byte[]>();
        foreach (var k in order)
        {
            if (repl.TryGetValue(k, out var r)) payload[k] = r.payload;
            else payload[k] = Data[(int)Items[k].Offset..(int)(Items[k].Offset + spans[k])];
        }
        var newOffset = new Dictionary<int, uint>();
        var data = new List<byte>();
        foreach (var k in order)
        {
            int align = Items[k].Align;
            while (data.Count % align != 0) data.Add(0);
            newOffset[k] = (uint)data.Count;
            data.AddRange(payload[k]);
        }
        while (data.Count % 16 != 0) data.Add(0);
        // pointer patch offsets move with the item that contains them
        uint Move(uint old)
        {
            int owner = order.Last(k => Items[k].Offset <= old);
            return newOffset[owner] + (old - Items[owner].Offset);
        }
        for (int i = 0; i < Patches.Count; i++)
            Patches[i] = (Patches[i].type, Patches[i].offsets.Select(Move).ToList());
        foreach (var k in order)
        {
            Items[k].Offset = newOffset[k];
            if (repl.TryGetValue(k, out var r)) Items[k].Count = r.count;
        }
        Data = data.ToArray();
    }

    public byte[] Save()
    {
        dataChunk.Body = Data;
        var ib = new List<byte>();
        foreach (var it in Items) { ib.AddRange(BitConverter.GetBytes(it.Word0)); ib.AddRange(BitConverter.GetBytes(it.Offset)); ib.AddRange(BitConverter.GetBytes(it.Count)); }
        itemChunk.Body = ib.ToArray();
        var pb = new List<byte>();
        foreach (var (type, offs) in Patches) { pb.AddRange(BitConverter.GetBytes(type)); pb.AddRange(BitConverter.GetBytes((uint)offs.Count)); foreach (var o in offs) pb.AddRange(BitConverter.GetBytes(o)); }
        patchChunk.Body = pb.ToArray();
        var o2 = new List<byte>();
        WriteChunk(Root, o2);
        return o2.ToArray();
    }
}
