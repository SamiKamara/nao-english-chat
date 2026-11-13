using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace NaoOpenAIChat;

public sealed record WaveAudio(byte[] Bytes, int DataOffset, int DataLength, int SampleRate, int Channels)
{
    public double DurationSeconds => DataLength / (double)(SampleRate * Channels * 2);
    public double Rms
    {
        get
        {
            double sum = 0;
            for (var i = DataOffset; i + 1 < DataOffset + DataLength; i += 2) { var value = BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(i, 2)); sum += (double)value * value; }
            return DataLength == 0 ? 0 : Math.Sqrt(sum / (DataLength / 2));
        }
    }
    public static WaveAudio FromRobot(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 44 or > 1_100_000)
            throw new InvalidDataException("NAO returned an empty or oversized audio recording.");
        var bytes = new byte[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetInt32(out var number) || number is < -128 or > 255) throw new InvalidDataException("NAO returned invalid audio bytes.");
            bytes[index++] = unchecked((byte)number);
        }
        return Parse(bytes);
    }
    public static WaveAudio Parse(byte[] bytes)
    {
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("The recording is not a WAV audio file.");
        var channels = 0; var rate = 0; var dataOffset = 0; var length = 0; var formatFound = false;
        for (var offset = 12; offset <= bytes.Length - 8;)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize > bytes.Length - offset - 8) throw new InvalidDataException("NAO returned a truncated WAV file.");
            var size = (int)chunkSize;
            var kind = Encoding.ASCII.GetString(bytes, offset, 4);
            if (kind == "fmt ")
            {
                if (size < 16 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 8, 2)) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 22, 2)) != 16)
                    throw new InvalidDataException("NAO must record uncompressed 16-bit audio.");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 10, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
                formatFound = true;
            }
            if (kind == "data") { dataOffset = offset + 8; length = size; }
            offset = checked(offset + 8 + size + (size & 1));
        }
        if (!formatFound || channels != 1 || rate != 16000 || dataOffset == 0 || length % 2 != 0)
            throw new InvalidDataException("NAO's recording must be 16 kHz mono PCM audio.");
        return new(bytes, dataOffset, length, rate, channels);
    }
    public WaveAudio Trim(double fromSeconds, double toSeconds)
    {
        var start = Math.Clamp((int)(fromSeconds * SampleRate), 0, DataLength / 2);
        var end = Math.Clamp((int)(toSeconds * SampleRate), start, DataLength / 2);
        var count = (end - start) * 2;
        var bytes = new byte[44 + count];
        using var writer = new BinaryWriter(new MemoryStream(bytes));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count); writer.Write(Bytes, DataOffset + start * 2, count);
        return Parse(bytes);
    }
}
