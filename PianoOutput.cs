using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace MidiKey;

internal sealed record PianoSettings(bool Enabled, int LeftOctaves, int RightOctaves, bool KickEnabled = false,
    int Sustain = PianoSettings.DefaultSustain, PianoAreaSetting[]? Areas = null, PianoModeKey? ModeKey = null)
{
    internal const int OctaveLimit = 4;
    internal const int DefaultSustain = 25;
    internal static readonly PianoSettings Default = new(false, 0, 0);

    internal PianoSettings Normalize() => this with
    {
        LeftOctaves = Math.Clamp(LeftOctaves, -OctaveLimit, OctaveLimit),
        RightOctaves = Math.Clamp(RightOctaves, -OctaveLimit, OctaveLimit),
        Sustain = Math.Clamp(Sustain, 0, 100),
        ModeKey = ModeKey is { Number: >= 0 and <= 127 } key && !string.IsNullOrEmpty(key.Port) ? key : null,
        Areas = Areas?.Where(area => area is not null && !string.IsNullOrEmpty(area.Port) &&
            area.Start >= 0 && area.End <= 127 && area.Start <= area.End)
            .Select(area => area with { Octaves = Math.Clamp(area.Octaves, -OctaveLimit, OctaveLimit) }).ToArray()
    };

    internal PianoSettings SetArea(string port, PianoArea area)
    {
        var updated = new List<PianoAreaSetting>();
        foreach (var saved in Areas ?? Array.Empty<PianoAreaSetting>())
        {
            if (saved.Port != port || saved.End < area.Start || saved.Start > area.End)
            {
                updated.Add(saved);
                continue;
            }
            if (saved.Start < area.Start) updated.Add(saved with { End = area.Start - 1 });
            if (saved.End > area.End) updated.Add(saved with { Start = area.End + 1 });
        }
        updated.Add(new(port, area.Start, area.End, area.Enabled, area.Octaves));
        return this with { Areas = updated.ToArray() };
    }
}

internal sealed record PianoAreaSetting(string Port, int Start, int End, bool Enabled, int Octaves);
internal sealed record PianoModeKey(string Port, int Number, SignalKind Kind = SignalKind.Note, int Channel = 0);

internal readonly record struct PianoArea(int Start, int End, bool Enabled, int Octaves)
{
    internal bool Contains(int note) => note >= Start && note <= End;
}

internal sealed record PianoRegions(PianoSettings Settings, PianoArea[] Areas, int? SwitchNote = null)
{
    internal bool HasSound => Areas.Any(area => area.Enabled);

    internal static PianoRegions ForPreset(PianoSettings settings, string? port, IEnumerable<Rule> rules)
    {
        if (port is null) return new(settings, Array.Empty<PianoArea>());
        var mapped = rules.Where(rule => rule.Kind == SignalKind.Note && rule.Port == port)
            .Select(rule => rule.Number).Distinct().OrderBy(note => note).ToArray();
        var saved = settings.Areas?.Where(area => area.Port == port)
            .OrderBy(area => area.Start).ToArray() ?? Array.Empty<PianoAreaSetting>();
        var areas = new List<PianoArea>();
        int start = 0;
        foreach (int boundary in mapped.Append(128))
        {
            if (start < boundary)
            {
                int end = boundary - 1;
                // After a range splits or merges, retain the setting with the greatest overlap.
                PianoAreaSetting? match = null;
                int longest = 0;
                foreach (var area in saved)
                {
                    int overlap = Math.Min(end, area.End) - Math.Max(start, area.Start) + 1;
                    if (overlap <= longest) continue;
                    match = area;
                    longest = overlap;
                }
                int legacyOctaves = mapped.Length == 0
                    ? settings.LeftOctaves == settings.RightOctaves ? settings.LeftOctaves : 0
                    : start == 0 ? settings.LeftOctaves : end == 127 ? settings.RightOctaves : 0;
                areas.Add(new(start, end, match?.Enabled ?? settings.Enabled, match?.Octaves ?? legacyOctaves));
            }
            start = boundary + 1;
        }
        var switchNote = settings.ModeKey is { Kind: SignalKind.Note } key && key.Port == port ? key.Number : (int?)null;
        return new(settings, areas.ToArray(), switchNote);
    }

    internal PianoArea? Find(int note)
    {
        foreach (var area in Areas)
            if (area.Contains(note)) return area;
        return null;
    }

    internal int? Pitch(int note, bool fullKeyboard = false)
    {
        if (note == SwitchNote) return null;
        if (fullKeyboard) return note;
        if (Find(note) is not PianoArea { Enabled: true } area) return null;
        int output = note + 12 * area.Octaves;
        return output is >= 0 and <= 127 ? output : null;
    }
}

internal sealed class PianoOutput : IDisposable
{
    private Engine? engine;
    private readonly object inputLock = new();
    private string? inputPort;
    private bool fullKeyboard, learning;
    private int switchHeldChannels, capturedHeldChannels;
    private int? capturedNote;
    internal PianoRegions Regions { get; private set; } = new(PianoSettings.Default, Array.Empty<PianoArea>());
    internal string? Error => engine?.Error;

    internal bool Configure(PianoRegions regions, string? port, bool reload = false)
    {
        Engine? retired = null;
        bool resetMode;
        lock (inputLock)
        {
            var previous = Regions;
            resetMode = reload || inputPort != port || previous.Settings.ModeKey != regions.Settings.ModeKey;
            if (resetMode)
            {
                fullKeyboard = learning = false;
                switchHeldChannels = !reload && inputPort == port
                    ? regions.Settings.ModeKey?.Kind == SignalKind.Control ? switchHeldChannels :
                        capturedNote == regions.SwitchNote ? capturedHeldChannels : 0
                    : 0;
            }
            else if (capturedNote is int captured && captured == regions.SwitchNote)
                switchHeldChannels = capturedHeldChannels;
            if (resetMode || capturedNote == regions.SwitchNote)
            {
                capturedNote = null;
                capturedHeldChannels = 0;
            }
            bool resetAudio = resetMode || !previous.Areas.AsSpan().SequenceEqual(regions.Areas);
            Regions = regions;
            inputPort = port;
            bool canPlay = port is not null && (regions.HasSound || regions.Settings.ModeKey is not null);
            if (!canPlay || engine?.Error is not null)
            {
                retired = engine;
                engine = null;
            }
            if (canPlay)
            {
                if (engine is null) engine = new Engine(regions, fullKeyboard);
                else if (resetAudio || previous.Settings.KickEnabled != regions.Settings.KickEnabled ||
                    previous.Settings.Sustain != regions.Settings.Sustain) engine.Configure(regions, fullKeyboard, resetAudio);
            }
        }
        retired?.Dispose();
        return resetMode;
    }

    internal void PreviewSustain(int value)
    {
        var updated = Regions with { Settings = Regions.Settings with { Sustain = value } };
        Configure(updated, inputPort);
    }

    internal void SetLearning(bool value)
    {
        lock (inputLock)
        {
            learning = value;
            capturedNote = null;
            capturedHeldChannels = 0;
        }
    }

    internal PianoInputAction Enqueue(uint raw, string? port = null)
    {
        // Switch playback at the MIDI packet boundary, before the UI consumes the same packet.
        uint status = raw & 0xf0;
        int number = (int)(raw >> 8) & 127;
        bool note = status is 0x80 or 0x90;
        bool control = status == 0xb0;
        if (!note && !control) return PianoInputAction.None;
        int channelMask = 1 << ((int)raw & 15);
        bool pressed = note ? status == 0x90 && ((raw >> 16) & 127) != 0 : ((raw >> 16) & 127) >= 64;
        lock (inputLock)
        {
            var selected = Regions.Settings.ModeKey;
            bool matches = selected is not null && selected.Port == (port ?? inputPort) &&
                selected.Kind == (note ? SignalKind.Note : SignalKind.Control) && selected.Number == number &&
                (selected.Channel == 0 || selected.Channel == ((int)raw & 15) + 1);
            if (learning && pressed && (note || control))
            {
                learning = false;
                if (note) { capturedNote = number; capturedHeldChannels = channelMask; }
                else switchHeldChannels = channelMask;
                return PianoInputAction.Learn;
            }
            if (note && capturedNote == number)
            {
                capturedHeldChannels = pressed ? capturedHeldChannels | channelMask : capturedHeldChannels & ~channelMask;
                if (!pressed && number == Regions.SwitchNote) switchHeldChannels &= ~channelMask;
                return PianoInputAction.Reserved;
            }
            if (matches)
            {
                bool wasHeld = switchHeldChannels != 0;
                switchHeldChannels = pressed ? switchHeldChannels | channelMask : switchHeldChannels & ~channelMask;
                if (!pressed || wasHeld) return PianoInputAction.Reserved;
                fullKeyboard = !fullKeyboard;
                engine?.Configure(Regions, fullKeyboard, reset: true);
                return fullKeyboard ? PianoInputAction.EnterFull : PianoInputAction.LeaveFull;
            }
            if (status == 0xb0 && number is 120 or 123) switchHeldChannels &= ~channelMask;
            if (port is null || port == inputPort) engine?.Enqueue(raw);
            return PianoInputAction.None;
        }
    }

    public void Dispose()
    {
        Engine? retired;
        lock (inputLock)
        {
            retired = engine;
            engine = null;
            fullKeyboard = learning = false;
            switchHeldChannels = capturedHeldChannels = 0;
            capturedNote = null;
            Regions = new(PianoSettings.Default, Array.Empty<PianoArea>());
            inputPort = null;
        }
        retired?.Dispose();
    }

    private sealed class Engine : IDisposable
    {
        private readonly record struct Command(uint Raw, PianoRegions? Regions = null, bool Full = false, bool Reset = false);
        private readonly ConcurrentQueue<Command> commands = new();
        private readonly ManualResetEvent stop = new(false);
        private readonly Thread worker;
        private readonly Dictionary<SignalId, int> notePitches = new();
        private readonly Voice?[] heldPitches = new Voice[128];
        private readonly int[] pitchOwners = new int[128];
        private readonly List<Voice> voices = new(64);
        private PianoRegions regions;
        private bool fullKeyboard;
        private PianoSampleBank bank = null!;
        private string? error;
        private int sustainChannels, kickCooldown;
        internal string? Error => Volatile.Read(ref error);

        internal Engine(PianoRegions regions, bool fullKeyboard)
        {
            this.regions = regions;
            this.fullKeyboard = fullKeyboard;
            worker = new Thread(Run) { IsBackground = true, Name = "GamusicStudio Audio" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private void Run()
        {
            try
            {
                bank = PianoSampleBank.Load();
                SharedAudioOutput.Run(stop, Mix);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException or
                COMException or InvalidOperationException or ArgumentException or InvalidCastException)
            {
                Volatile.Write(ref error, $"钢琴音频播放失败 · {ex.Message}");
            }
        }

        internal void Configure(PianoRegions updated, bool full, bool reset) => commands.Enqueue(new(0, updated, full, reset));

        // The WinMM callback only queues data. Audio rendering never uses the UI dispatcher.
        internal void Enqueue(uint raw)
        {
            if (Error is null) commands.Enqueue(new(raw));
        }

        private void Mix(float[] buffer, int frames, int rate)
        {
            while (commands.TryDequeue(out var command))
            {
                if (command.Regions is PianoRegions updated)
                {
                    if (command.Reset) Reset();
                    else if (!updated.Settings.KickEnabled) StopKick();
                    regions = updated;
                    fullKeyboard = command.Full;
                }
                else if (MidiSignal.Decode(command.Raw) is MidiSignal signal) Handle(signal, rate);
            }
            Array.Clear(buffer, 0, frames);
            for (int i = voices.Count - 1; i >= 0; i--)
                if (!voices[i].Mix(buffer, frames)) voices.RemoveAt(i);
            for (int i = 0; i < frames; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            kickCooldown = Math.Max(0, kickCooldown - frames);
        }

        private void Handle(MidiSignal signal, int rate)
        {
            if (signal.Id.Kind == SignalKind.Note)
            {
                Release(signal.Id);
                if (signal.Value == 0 || regions.Pitch(signal.Id.Number, fullKeyboard) is not int pitch) return;
                notePitches[signal.Id] = pitch;
                if (pitchOwners[pitch]++ == 0)
                {
                    float velocity = signal.Value / 127f;
                    heldPitches[pitch] = AddVoice(bank.Piano[pitch], pitch, rate,
                        0.62f * velocity * (0.35f + 0.65f * velocity));
                }
                if (regions.Settings.KickEnabled && kickCooldown == 0)
                {
                    float gain = 0.50f + signal.Value * 0.002f;
                    AddVoice(bank.Kick, 36, rate, gain, drum: true);
                    AddVoice(bank.Kick, 36, rate, gain * 0.40f, rate * 135 / 1000, drum: true);
                    AddVoice(bank.Kick, 36, rate, gain * 0.22f, rate * 268 / 1000, drum: true);
                    kickCooldown = rate * 85 / 1000;
                }
            }
            else if (signal.Id.Number == 64) Sustain(signal.Id.Channel, signal.Value >= 64);
            else
            {
                foreach (var note in notePitches.Keys.Where(note => note.Channel == signal.Id.Channel).ToArray())
                    Release(note);
                Sustain(signal.Id.Channel, false);
            }
        }

        private Voice AddVoice(PianoSampleBank.Sample sample, int note, int rate, float gain, int delay = 0, bool drum = false)
        {
            if (voices.Count == 64) voices.RemoveAt(0);
            var voice = new Voice(sample, note, rate, gain, delay, drum);
            voices.Add(voice);
            return voice;
        }

        private void Release(SignalId note)
        {
            if (!notePitches.Remove(note, out int pitch) || --pitchOwners[pitch] > 0) return;
            if (heldPitches[pitch] is Voice voice)
            {
                voice.KeyDown = false;
                if (sustainChannels == 0) voice.Release(regions.Settings.Sustain);
            }
            heldPitches[pitch] = null;
        }

        private void Sustain(int channel, bool active)
        {
            int mask = 1 << (channel - 1);
            sustainChannels = active ? sustainChannels | mask : sustainChannels & ~mask;
            if (!active && sustainChannels == 0)
                foreach (var voice in voices)
                    if (!voice.Drum && !voice.KeyDown) voice.Release(regions.Settings.Sustain);
        }

        private void StopKick()
        {
            voices.RemoveAll(voice => voice.Drum);
            kickCooldown = 0;
        }

        private void Reset()
        {
            voices.Clear();
            notePitches.Clear();
            Array.Clear(heldPitches);
            Array.Clear(pitchOwners);
            sustainChannels = kickCooldown = 0;
        }

        public void Dispose()
        {
            stop.Set();
            worker.Join();
            stop.Dispose();
        }
    }

    private sealed class Voice
    {
        private readonly PianoSampleBank.Sample sample;
        private readonly double step;
        private readonly float loopDecay;
        private readonly int rate;
        private float gain;
        private double position;
        private int delay, releaseFrames, remainingRelease;
        private bool releasing;
        internal bool KeyDown { get; set; } = true;
        internal bool Drum { get; }

        internal Voice(PianoSampleBank.Sample sample, int note, int rate, float gain, int delay, bool drum)
        {
            this.sample = sample;
            this.rate = rate;
            this.gain = gain;
            this.delay = delay;
            Drum = drum;
            step = (double)sample.Rate / rate * Math.Pow(2, (note - sample.Root - sample.FineTune / 100d) / 12d);
            // The recorded piano attack decays on its own. Fade the sustained loop as a real string would.
            loopDecay = (float)Math.Exp(-1 / (rate * (note < 60 ? 2.3 : 1.3)));
        }

        internal void Release(int sustain)
        {
            if (releasing) return;
            releasing = true;
            double amount = sustain / 100d;
            releaseFrames = (int)(rate * (0.025 + 1.44 * amount * amount));
            remainingRelease = releaseFrames;
        }

        internal bool Mix(float[] buffer, int frames)
        {
            int start = Math.Min(delay, frames);
            delay -= start;
            int loopEnd = sample.LoopStart + sample.LoopLength;
            for (int i = start; i < frames; i++)
            {
                if (sample.LoopLength > 0 && position >= loopEnd)
                    position = sample.LoopStart + (position - loopEnd) % sample.LoopLength;
                if (position >= sample.Data.Length || gain < 0.0001f ||
                    (releasing && remainingRelease == 0)) return false;
                int a = (int)position;
                int b = sample.LoopLength > 0 && a + 1 == loopEnd ? sample.LoopStart : Math.Min(a + 1, sample.Data.Length - 1);
                float value = sample.Data[a] + (sample.Data[b] - sample.Data[a]) * (float)(position - a);
                float envelope = releasing ? (float)remainingRelease-- / releaseFrames : 1f;
                buffer[i] += value / 32768f * gain * envelope;
                position += step;
                if (sample.LoopLength > 0 && a >= sample.LoopStart) gain *= loopDecay;
            }
            return true;
        }
    }
}

