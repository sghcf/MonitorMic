namespace MonitorMicWin;

/// <summary>Applies a bounded software gain to interleaved little-endian PCM16.</summary>
internal static class PcmGain
{
    public const float Default = 8f;
    public const float Min = 1f;
    public const float Max = 16f;

    public static float Clamp(float value) => Math.Clamp(value, Min, Max);

    public static void ApplyInPlace(Span<byte> pcm, float gain)
    {
        gain = Clamp(gain);
        if (Math.Abs(gain - 1f) < 0.0001f) return;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (int)MathF.Round(sample * gain);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            pcm[i] = (byte)(scaled & 0xff);
            pcm[i + 1] = (byte)((scaled >> 8) & 0xff);
        }
    }
}
