using System;
using System.IO;
using System.Text;

namespace Yursic.Audio
{
    /// <summary>Minimal RIFF/WAVE reader and writer (PCM 8/16/24/32-bit and 32-bit float, mono or multichannel).</summary>
    public static class WavFile
    {
        private const ushort FormatPcm = 1;
        private const ushort FormatFloat = 3;
        private const ushort FormatExtensible = 0xFFFE;

        public static SampleBuffer Read(Stream stream)
        {
            using (var r = new BinaryReader(stream, Encoding.ASCII, true))
            {
                if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
                r.ReadUInt32();
                if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

                ushort format = 0, channels = 0, bits = 0;
                int rate = 0;
                byte[] data = null;

                while (stream.Position + 8 <= stream.Length)
                {
                    string id = new string(r.ReadChars(4));
                    uint size = r.ReadUInt32();
                    long next = stream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        format = r.ReadUInt16();
                        channels = r.ReadUInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32(); // byte rate
                        r.ReadUInt16(); // block align
                        bits = r.ReadUInt16();
                        if (format == FormatExtensible && size >= 40)
                        {
                            r.ReadUInt16(); // cbSize
                            r.ReadUInt16(); // valid bits
                            r.ReadUInt32(); // channel mask
                            format = r.ReadUInt16(); // first two bytes of the sub-format GUID
                        }
                    }
                    else if (id == "data")
                    {
                        long avail = Math.Min(size, stream.Length - stream.Position);
                        data = r.ReadBytes((int)avail);
                    }
                    if (next > stream.Length) break;
                    stream.Position = next;
                }

                if (data == null || channels == 0 || rate == 0)
                    throw new InvalidDataException("WAV file is missing fmt or data.");
                if (format != FormatPcm && format != FormatFloat)
                    throw new InvalidDataException("Unsupported WAV encoding (only PCM and float).");

                int bytesPer = bits / 8;
                long frames = data.Length / (bytesPer * channels);
                var o = new float[frames * 2];
                int idx = 0;
                for (long f = 0; f < frames; f++)
                {
                    float l = Sample(data, idx, bits, format);
                    float rr = channels > 1 ? Sample(data, idx + bytesPer, bits, format) : l;
                    o[2 * f] = l;
                    o[2 * f + 1] = rr;
                    idx += bytesPer * channels;
                }
                return new SampleBuffer(o, rate);
            }
        }

        private static float Sample(byte[] d, int i, int bits, ushort format)
        {
            if (format == FormatFloat)
                return bits == 64 ? (float)BitConverter.ToDouble(d, i) : BitConverter.ToSingle(d, i);
            switch (bits)
            {
                case 8: return (d[i] - 128) / 128f;
                case 16: return BitConverter.ToInt16(d, i) / 32768f;
                case 24: return ((d[i] | (d[i + 1] << 8) | ((sbyte)d[i + 2] << 16))) / 8388608f;
                case 32: return BitConverter.ToInt32(d, i) / 2147483648f;
                default: throw new InvalidDataException("Unsupported bit depth " + bits);
            }
        }

        /// <summary>Writes stereo interleaved floats as 16-bit PCM (or 32-bit float if requested).</summary>
        public static void Write(Stream stream, float[] stereo, int sampleRate, bool asFloat = false)
        {
            int bits = asFloat ? 32 : 16;
            int channels = 2;
            int blockAlign = channels * bits / 8;
            int dataBytes = stereo.Length * bits / 8;
            using (var w = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write(asFloat ? FormatFloat : FormatPcm);
                w.Write((ushort)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * blockAlign);
                w.Write((ushort)blockAlign);
                w.Write((ushort)bits);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);

                var chunk = new byte[64 * 1024];
                int pos = 0;
                foreach (var s in stereo)
                {
                    float c = s > 1f ? 1f : (s < -1f ? -1f : s);
                    if (asFloat)
                    {
                        var b = BitConverter.GetBytes(c);
                        Buffer.BlockCopy(b, 0, chunk, pos, 4);
                        pos += 4;
                    }
                    else
                    {
                        short v = (short)Math.Round(c * 32767f);
                        chunk[pos++] = (byte)(v & 0xFF);
                        chunk[pos++] = (byte)((v >> 8) & 0xFF);
                    }
                    if (pos >= chunk.Length - 4)
                    {
                        w.Write(chunk, 0, pos);
                        pos = 0;
                    }
                }
                if (pos > 0) w.Write(chunk, 0, pos);
            }
        }
    }
}
