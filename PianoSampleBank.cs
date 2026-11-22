using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MidiKey;

internal sealed class PianoSampleBank
{
    internal sealed record Sample(short[] Data, int Rate, int Root, int FineTune, int LoopStart, int LoopLength);
    private readonly record struct Chunk(string Id, int Offset, int End)
    {
        internal int Length => End - Offset;
    }

    internal Sample[] Piano { get; }
    internal Sample Kick { get; }

    private PianoSampleBank(Sample[] piano, Sample kick)
    {
        Piano = piano;
        Kick = kick;
    }

    internal static PianoSampleBank Load()
    {
        string path = Path.Combine(Environment.SystemDirectory, "drivers", "gm.dls");
        var reader = new Reader(File.ReadAllBytes(path));
        return reader.Load();
    }

    // DLS samples are referenced by offsets in the wave pool, not by instrument order.
    private sealed class Reader
    {
        private readonly byte[] bytes;

        internal Reader(byte[] bytes) => this.bytes = bytes;

        internal PianoSampleBank Load()
        {
            var root = Find(new("", 0, bytes.Length), "DLS ");
            var instruments = Find(root, "lins");
            var waves = Find(root, "wvpl");
            var table = Find(root, "ptbl", 8);
            int tableStart = checked(table.Offset + (int)U32(table.Offset));
            uint cueCount = U32(table.Offset + 4);
            var piano = new Sample[128];
            Sample? kick = null;
            foreach (var instrument in Children(instruments))
            {
                if (instrument.Id != "ins ") continue;
                var header = Find(instrument, "insh", 12);
                uint bank = U32(header.Offset + 4);
                if (U32(header.Offset + 8) != 0 || (bank != 0 && bank != 0x80000000)) continue;
                foreach (var region in Children(Find(instrument, "lrgn")))
                {
                    if (region.Id is not ("rgn " or "rgn2")) continue;
                    var range = Find(region, "rgnh", 8);
                    int low = U16(range.Offset), high = U16(range.Offset + 2);
                    if (high > 127 || low > high) throw new InvalidDataException("无效的采样音域");
                    if (bank != 0 && (36 < low || 36 > high)) continue;

                    var link = Find(region, "wlnk", 12);
                    uint index = U32(link.Offset + 8);
                    long cue = tableStart + 4L * index;
                    if (index >= cueCount || cue < table.Offset + 8 || cue + 4 > table.End)
                        throw new InvalidDataException("无效的采样索引");
                    int waveOffset = checked(waves.Offset + (int)U32((int)cue));
                    var wave = Find(waves with { Offset = waveOffset }, "wave");
                    var sample = ReadSample(region, wave);
                    if (bank != 0) kick = sample;
                    else Array.Fill(piano, sample, low, high - low + 1);
                }
            }
            if (kick is null || Array.Exists(piano, sample => sample is null))
                throw new InvalidDataException("Windows 音源缺少钢琴或底鼓采样");
            return new(piano, kick);
        }

        private Sample ReadSample(Chunk region, Chunk wave)
        {
            var format = Find(wave, "fmt ", 16);
            int rate = checked((int)U32(format.Offset + 4));
            if (U16(format.Offset) != 1 || U16(format.Offset + 2) != 1 ||
                U16(format.Offset + 14) != 16 || rate <= 0)
                throw new InvalidDataException("Windows 音源使用了不支持的采样格式");

            var data = Find(wave, "data", 4);
            var pcm = new short[data.Length / 2];
            Buffer.BlockCopy(bytes, data.Offset, pcm, 0, pcm.Length * sizeof(short));
            var metadata = Children(region).FirstOrDefault(chunk => chunk.Id == "wsmp");
            if (metadata.Id is null) metadata = Find(wave, "wsmp", 20);
            if (metadata.Length < 20) throw new InvalidDataException("无效的采样参数");
            int root = U16(metadata.Offset + 4), fine = (short)U16(metadata.Offset + 6);
            int loopStart = 0, loopLength = 0;
            if (U32(metadata.Offset + 16) > 0)
            {
                long loop = metadata.Offset + (long)U32(metadata.Offset);
                if (loop >= metadata.Offset + 20 && loop + 16 <= metadata.End && U32((int)loop + 4) == 0)
                {
                    uint start = U32((int)loop + 8), length = U32((int)loop + 12);
                    if (length > 1 && start + (long)length <= pcm.Length)
                    {
                        loopStart = (int)start;
                        loopLength = (int)length;
                    }
                }
            }
            return new(pcm, rate, root, fine, loopStart, loopLength);
        }

        private Chunk Find(Chunk parent, string id, int minimum = 0)
        {
            foreach (var chunk in Children(parent))
                if (chunk.Id == id && chunk.Length >= minimum) return chunk;
            throw new InvalidDataException($"Windows 音源缺少 {id.Trim()} 数据");
        }

        private IEnumerable<Chunk> Children(Chunk parent)
        {
            for (int header = parent.Offset; header <= parent.End - 8;)
            {
                int start = header + 8;
                long length = U32(header + 4);
                if (length > parent.End - start) throw new InvalidDataException("无效的音源数据长度");
                int end = start + (int)length;
                string id = Encoding.ASCII.GetString(bytes, header, 4);
                if ((id == "LIST" || id == "RIFF") && length >= 4)
                {
                    id = Encoding.ASCII.GetString(bytes, start, 4);
                    start += 4;
                }
                yield return new(id, start, end);
                header = checked(end + ((int)length & 1));
            }
        }

        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }
}
