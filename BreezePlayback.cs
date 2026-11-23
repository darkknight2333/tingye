using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Tingye
{
    internal enum NeuralVoice { BreezeBlue, BreezeLocal, QwenVivian, QwenSerena }

    internal sealed class BreezePlayback
    {
        private const int LocalParagraphLimit = 3;
        private const int LocalClipLimit = 6;
        private const int LocalPrimeClipLimit = 3;
        private const int MinimumPrimeMs = 3000;
        private const int MaximumPrimeMs = 6500;
        private readonly BreezeVoice voice;
        private CancellationTokenSource sequence;
        private PcmPlayback output;
        private TaskCompletionSource<bool> resumeSignal;
        private bool generating, primed, localPlayback;
        private bool? reportedBuffering;

        public BreezePlayback(BreezeVoice voice) { this.voice = voice; }
        public bool Active { get { return sequence != null; } }
        public bool Buffering { get { return Active && (!primed || (generating && output.Pending == 0)); } }
        public event Action Changed;
        public event Action<int, int> PassageStarted;
        public event Action<Exception> Completed;

        public async void Speak(Book book, int chapter, int passage, float rate, NeuralVoice voiceChoice)
        {
            bool useLocal = voiceChoice != NeuralVoice.BreezeBlue;
            localPlayback = useLocal;
            reportedBuffering = null;
            var current = sequence = new CancellationTokenSource();
            var token = current.Token;
            var audio = output = new PcmPlayback();
            audio.Pause();
            var queued = new Queue<QueuedClip>();
            long readyBytes = 0;
            long primeMs = MinimumPrimeMs;
            bool streamLocal = voiceChoice == NeuralVoice.BreezeLocal;
            var dispatcher = Dispatcher.CurrentDispatcher;
            int streamLeadMs = 900;
            audio.Drained += delegate {
                if (sequence != current) return;
                if (streamLocal && generating && primed)
                {
                    audio.Pause();
                    primed = false;
                    readyBytes = 0;
                    streamLeadMs = Math.Min(3000, streamLeadMs + 700);
                }
                Notify();
            };
            Exception error = null;
            try
            {
                if (useLocal) voice.SetLocalPlaybackActive(true);
                await Task.Yield();
                int paragraphNumber = 0;
                for (; chapter < book.Chapters.Count; chapter++, passage = 0)
                {
                    var paragraphs = book.Chapters[chapter].Passages;
                    for (; passage < paragraphs.Count; passage++)
                    {
                        token.ThrowIfCancellationRequested();
                        bool quickStart = streamLocal && paragraphNumber == 0;
                        var pieces = SpeechText.Split(book.SpeechAt(chapter, passage), !useLocal, quickStart);
                        if (pieces.Count == 0) continue;
                        int currentChapter = chapter, currentPassage = passage;
                        for (int i = 0; i < pieces.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            while (queued.Count != 0 &&
                                (queued.Peek().Done.IsCompleted || QueueFull(queued, paragraphNumber, useLocal)))
                                await queued.Dequeue().Done;
                            token.ThrowIfCancellationRequested();
                            while (resumeSignal != null)
                            {
                                await resumeSignal.Task;
                                token.ThrowIfCancellationRequested();
                            }
                            token.ThrowIfCancellationRequested();
                            generating = true;
                            Notify();
                            bool paragraphEnd = i + 1 == pieces.Count;
                            Action started = i == 0 ? (Action)(() => {
                                if (PassageStarted != null) PassageStarted(currentChapter, currentPassage);
                            }) : null;
                            if (streamLocal)
                            {
                                Task lastPlayed = null;
                                Action<byte[]> submit = block => {
                                    token.ThrowIfCancellationRequested();
                                    lastPlayed = audio.Enqueue(block, started);
                                    started = null;
                                    if (!primed)
                                    {
                                        readyBytes += block.Length;
                                        if (readyBytes >= streamLeadMs * (BreezeVoice.SampleRate * 2 / 1000)) Prime();
                                    }
                                    Notify();
                                };
                                try
                                {
                                    var cached = await voice.SynthesizeAsync(pieces[i], paragraphEnd, rate, token, true,
                                        block => dispatcher.InvokeAsync(() => submit(block), DispatcherPriority.Normal, token).Task);
                                    token.ThrowIfCancellationRequested();
                                    if (cached != null) submit(cached);
                                    generating = false;
                                    // A complete short clip can play immediately even below the streaming threshold.
                                    if (lastPlayed != null) Prime();
                                    Notify();
                                }
                                finally
                                {
                                    if (lastPlayed != null) queued.Enqueue(new QueuedClip(paragraphNumber, lastPlayed));
                                }
                                continue;
                            }
                            long startedAt = Stopwatch.GetTimestamp();
                            byte[] clip = voiceChoice == NeuralVoice.QwenVivian || voiceChoice == NeuralVoice.QwenSerena
                                ? await voice.SynthesizeQwenAsync(pieces[i], paragraphEnd, rate, token, voiceChoice)
                                : await voice.SynthesizeAsync(pieces[i], paragraphEnd, rate, token, useLocal);
                            token.ThrowIfCancellationRequested();
                            generating = false;
                            queued.Enqueue(new QueuedClip(paragraphNumber, audio.Enqueue(clip, started)));
                            if (!primed)
                            {
                                readyBytes += clip.Length;
                                if (useLocal)
                                {
                                    long synthesisMs = (Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency;
                                    primeMs = Math.Max(primeMs, Math.Min(MaximumPrimeMs, synthesisMs * 3 / 2));
                                }
                                int clipLimit = useLocal ? LocalPrimeClipLimit : 2;
                                if (queued.Count >= clipLimit || readyBytes * 1000 >= primeMs * BreezeVoice.SampleRate * 2)
                                    Prime();
                            }
                            Notify();
                        }
                        paragraphNumber++;
                    }
                }
            }
            catch (Exception ex) { error = ex; }
            if (sequence != current) return;
            generating = false;
            // A failed lookahead should not cut off audio already submitted.
            try
            {
                if (queued.Count != 0) Prime();
                while (queued.Count != 0) await queued.Dequeue().Done;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (error == null) error = ex; }
            if (sequence != current) return;
            try { Stop(); }
            catch (Exception ex) { if (error == null) error = ex; }
            if (Completed != null) Completed(error);
        }

        private static bool QueueFull(Queue<QueuedClip> queued, int paragraphNumber, bool useLocal)
        {
            if (!useLocal) return queued.Count >= 2;
            // Keep room for the remaining clips of an admitted paragraph.
            return queued.Count >= LocalClipLimit ||
                (!queued.Any(clip => clip.Paragraph == paragraphNumber) &&
                 queued.Select(clip => clip.Paragraph).Distinct().Count() >= LocalParagraphLimit);
        }

        private sealed class QueuedClip
        {
            public readonly int Paragraph;
            public readonly Task Done;

            public QueuedClip(int paragraph, Task done)
            {
                Paragraph = paragraph;
                Done = done;
            }
        }

        private void Prime()
        {
            if (primed) return;
            primed = true;
            if (resumeSignal == null) output.Resume();
        }

        public void Pause()
        {
            if (resumeSignal == null)
                resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (primed && output != null) output.Pause();
            if (localPlayback) voice.SetLocalPlaybackActive(false);
        }

        public void Resume()
        {
            if (primed && output != null) output.Resume();
            if (localPlayback) voice.SetLocalPlaybackActive(true);
            ReleasePause();
        }

        public void Stop()
        {
            if (sequence == null) return;
            sequence.Cancel();
            sequence.Dispose();
            sequence = null;
            generating = primed = false;
            if (localPlayback) voice.SetLocalPlaybackActive(false);
            localPlayback = false;
            ReleasePause();
            var audio = output;
            output = null;
            audio.Dispose();
        }

        private void ReleasePause()
        {
            var waiting = resumeSignal;
            resumeSignal = null;
            if (waiting != null) waiting.TrySetResult(true);
        }

        private void Notify()
        {
            bool buffering = Buffering;
            if (reportedBuffering == buffering) return;
            reportedBuffering = buffering;
            if (Changed != null) Changed();
        }
    }
}
