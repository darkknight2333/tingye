using System;

namespace Tingye
{
    internal static class SpeechPauses
    {
        private const float QuietSample = 0.001f;

        // Compact in place, keeping every sample above the quiet threshold.
        public static int Shorten(float[] samples, int sampleRate, int tailSamples)
        {
            int firstSignal = 0;
            while (firstSignal < samples.Length && IsQuiet(samples[firstSignal])) firstSignal++;
            if (firstSignal == samples.Length) return 0;

            int signalEnd = samples.Length - TrailingQuiet(samples, samples.Length, samples.Length - firstSignal);
            int copyFrom = Math.Max(0, firstSignal - sampleRate * 20 / 1000);
            int written = 0;
            int longPause = sampleRate * 170 / 1000;
            int innerPause = sampleRate * 120 / 1000;
            for (int i = firstSignal; i < signalEnd;)
            {
                if (!IsQuiet(samples[i]))
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < signalEnd && IsQuiet(samples[i])) i++;
                if (i - start > longPause)
                {
                    int midpoint = innerPause / 2;
                    int copyUntil = start + midpoint;
                    int length = copyUntil - copyFrom;
                    Array.Copy(samples, copyFrom, samples, written, length);
                    written += length;
                    copyFrom = i - (innerPause - midpoint);
                }
            }
            int end = Math.Min(samples.Length, signalEnd + tailSamples);
            int remaining = end - copyFrom;
            Array.Copy(samples, copyFrom, samples, written, remaining);
            return written + remaining;
        }

        public static int TrailingQuiet(float[] samples, int end, int limit)
        {
            int count = 0;
            while (count < limit && count < end && IsQuiet(samples[end - 1 - count])) count++;
            return count;
        }

        private static bool IsQuiet(float sample)
        {
            return Math.Abs(sample) <= QuietSample;
        }
    }
}
