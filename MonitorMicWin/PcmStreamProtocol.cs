using System.Globalization;
using System.Text;

namespace MonitorMicWin;

/// <summary>MicStreamer TCP PCM stream format announced by the Android server.</summary>
public readonly record struct PcmStreamFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int FrameBytes => checked(Channels * (BitsPerSample / 8));

    public override string ToString() =>
        $"{SampleRate} Hz / {Channels} ch / {BitsPerSample} bit";

    internal static bool TryParse(ReadOnlySpan<byte> line, out PcmStreamFormat format)
    {
        format = default;
        if (line.Length == 0 || line.Length > 96) return false;

        // The wire header is ASCII. Reject replacement characters and control bytes
        // instead of accepting a visually similar or truncated header.
        for (var i = 0; i < line.Length; i++)
        {
            var b = line[i];
            if (b > 0x7f || b == 0 || b == '\t') return false;
        }

        var text = Encoding.ASCII.GetString(line).TrimEnd('\r').Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "PCM") return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var channels)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
            return false;

        // Android currently sends 48 kHz PCM16 mono/stereo. A bounded sane range
        // allows compatible servers while rejecting values that cannot be rendered.
        if (rate is < 8_000 or > 192_000 || channels is < 1 or > 2 || bits != 16)
            return false;

        format = new PcmStreamFormat(rate, channels, bits);
        return true;
    }
}

public sealed class PcmStreamProtocolException : Exception
{
    public PcmStreamProtocolException(string message) : base(message) { }
}

/// <summary>
/// Incremental parser for the MicStreamer header followed by little-endian PCM16.
/// It accepts arbitrary TCP fragmentation and emits only complete PCM frames.
/// </summary>
public sealed class PcmStreamParser
{
    public const int DefaultMaxHeaderBytes = 128;

    readonly byte[] headerBuffer;
    byte[] partialFrame = Array.Empty<byte>();
    int headerLength;

    public PcmStreamFormat? Format { get; private set; }
    public bool HeaderParsed => Format.HasValue;
    public int PendingPcmBytes => partialFrame.Length;

    public PcmStreamParser(int maxHeaderBytes = DefaultMaxHeaderBytes)
    {
        if (maxHeaderBytes < 16) throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
        headerBuffer = new byte[maxHeaderBytes];
    }

    /// <summary>Feeds bytes from any TCP read and invokes onPcm for complete frames.</summary>
    public void Feed(ReadOnlySpan<byte> input, Action<ReadOnlyMemory<byte>> onPcm)
    {
        ArgumentNullException.ThrowIfNull(onPcm);
        if (input.Length == 0) return;

        if (!HeaderParsed)
        {
            var newline = input.IndexOf((byte)'\n');
            var headerBytes = newline >= 0 ? newline + 1 : input.Length;
            if (headerLength + headerBytes > headerBuffer.Length)
                throw new PcmStreamProtocolException("PCM 头超过长度上限");

            input[..headerBytes].CopyTo(headerBuffer.AsSpan(headerLength));
            headerLength += headerBytes;

            if (newline < 0) return;

            var lineLength = headerLength - 1;
            if (!PcmStreamFormat.TryParse(headerBuffer.AsSpan(0, lineLength), out var format))
                throw new PcmStreamProtocolException("非法 PCM 头，期望 PCM <rate> <channels> 16\\n");
            Format = format;

            var remainder = input[(newline + 1)..];
            ProcessPcm(remainder, onPcm);
            return;
        }

        ProcessPcm(input, onPcm);
    }

    void ProcessPcm(ReadOnlySpan<byte> input, Action<ReadOnlyMemory<byte>> onPcm)
    {
        var frameBytes = Format!.Value.FrameBytes;
        var oldPartial = partialFrame;
        var total = checked(oldPartial.Length + input.Length);
        var completeLength = total - total % frameBytes;
        if (completeLength > 0)
        {
            var complete = new byte[completeLength];
            var oldToCopy = Math.Min(oldPartial.Length, completeLength);
            if (oldToCopy > 0)
            {
                oldPartial.AsSpan(0, oldToCopy).CopyTo(complete);
            }
            var inputToCopy = completeLength - oldToCopy;
            if (inputToCopy > 0)
                input[..inputToCopy].CopyTo(complete.AsSpan(oldToCopy));
            onPcm(complete);
        }

        var remaining = total - completeLength;
        if (remaining == 0)
        {
            partialFrame = Array.Empty<byte>();
            return;
        }

        partialFrame = new byte[remaining];
        var written = 0;
        if (completeLength < oldPartial.Length)
        {
            var oldRemainder = Math.Min(oldPartial.Length - completeLength, remaining);
            oldPartial.AsSpan(completeLength, oldRemainder).CopyTo(partialFrame);
            written = oldRemainder;
        }
        if (written < remaining)
        {
            var inputOffset = completeLength - oldPartial.Length;
            if (inputOffset < 0) inputOffset = 0;
            input.Slice(inputOffset, remaining - written).CopyTo(partialFrame.AsSpan(written));
        }
    }
}
