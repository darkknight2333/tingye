using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tingye
{
    internal sealed class NaturalPlayback
    {
        private readonly KokoroVoice voice;
        private CancellationTokenSource sequence;
        private Prefetch prepared;
        private PcmPlayback output;
        private bool paused;

        public NaturalPlayback(KokoroVoice voice) { this.voice = voice; }

        public bool Active { get; private set; }
        public bool Buffering { get; private set; }
        public event Action Changed;
        public event Action<Exception> Completed;

        public async void Speak(string text, string nextText, int speaker, float rate)
        {
            if (sequence == null) sequence = new CancellationTokenSource();
            var current = sequence;
            var token = current.Token;
            Active = true;
            paused = false;
            Exception error = null;
            try
            {
                // A silent paragraph can complete immediately. Let the dispatcher unwind before advancing.
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                var pieces = SpeechText.Split(text);
                var nextPieces = SpeechText.Split(nextText ?? "");
                for (int i = 0; i < pieces.Count; i++)
                {
                    SetBuffering(true);
                    var wave = await Prepare(pieces[i], i + 1 == pieces.Count, speaker, rate, token);
                    token.ThrowIfCancellationRequested();
                    if (!paused)
                    {
                        if (i + 1 < pieces.Count)
                            StartPrefetch(pieces[i + 1], i + 2 == pieces.Count, speaker, rate, token);
                        else if (nextPieces.Count > 0)
                            StartPrefetch(nextPieces[0], nextPieces.Count == 1, speaker, rate, token);
                    }
                    if (wave != null && wave.Length > 44) await PlayClip(wave);
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (Exception ex) { error = ex; }
            if (current != sequence) return;
            Active = false;
            SetBuffering(false);
            if (Completed != null) Completed(error);
        }

        public void Pause()
        {
            paused = true;
            if (output != null) output.Pause();
        }

        public void Resume()
        {
            paused = false;
            if (output != null) output.Resume();
        }

        public void Stop()
        {
            if (sequence != null)
            {
                sequence.Cancel();
                sequence.Dispose();
                sequence = null;
            }
            prepared = null;
            Active = false;
            Buffering = false;
            var audio = output;
            output = null;
            if (audio != null) audio.Dispose();
        }

        private Task<byte[]> Prepare(string text, bool paragraphEnd, int speaker, float rate, CancellationToken token)
        {
            if (prepared != null && prepared.Text == text && prepared.ParagraphEnd == paragraphEnd
                && prepared.Speaker == speaker && prepared.Rate == rate)
                return prepared.Audio;
            var audio = voice.SynthesizeAsync(text, paragraphEnd, speaker, rate, token);
            // A pending lookahead may be abandoned when the listener jumps or closes the app.
            audio.ContinueWith(failed => { GC.KeepAlive(failed.Exception); }, TaskContinuationOptions.OnlyOnFaulted);
            prepared = new Prefetch { Text = text, ParagraphEnd = paragraphEnd, Speaker = speaker, Rate = rate, Audio = audio };
            return audio;
        }

        private void StartPrefetch(string text, bool paragraphEnd, int speaker, float rate, CancellationToken token)
        {
            Prepare(text, paragraphEnd, speaker, rate, token);
        }

        private Task PlayClip(byte[] wave)
        {
            // Kokoro emits a fixed mono PCM WAV header followed by little-endian samples.
            if (output == null)
            {
                output = new PcmPlayback(BitConverter.ToInt32(wave, 24));
                if (paused) output.Pause();
            }
            var playback = output.Enqueue(wave, 44, null);
            SetBuffering(false);
            return playback;
        }

        private void SetBuffering(bool value)
        {
            if (Buffering == value) return;
            Buffering = value;
            if (Changed != null) Changed();
        }

        private sealed class Prefetch
        {
            public string Text;
            public bool ParagraphEnd;
            public int Speaker;
            public float Rate;
            public Task<byte[]> Audio;
        }
    }
}
