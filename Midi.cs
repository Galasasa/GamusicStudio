using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MidiKey;

internal enum SignalKind { Note, Control, Bend, ChannelPressure, PolyPressure, Program }

internal static class Signals
{
    internal static readonly string[] Names = { "音符 / Note", "控制器 / CC", "弯音 / Pitch Bend",
        "通道触后 / Channel Pressure", "复音触后 / Poly Pressure", "音色切换 / Program" };
    private static readonly string[] NoteNames = { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" };
    internal static int Maximum(SignalKind kind) => kind == SignalKind.Bend ? 16383 : 127;
    internal static int Rest(SignalKind kind) => kind == SignalKind.Bend ? 8192 : 0;
    internal static int Threshold(SignalKind kind) => kind switch
    {
        SignalKind.Bend => 8193,
        SignalKind.Control => 64,
        _ => 1
    };
    internal static int Count(SignalKind kind) => kind is SignalKind.Bend or SignalKind.ChannelPressure ? 1 : 128;
    internal static string Label(SignalKind kind, int number) => kind switch
    {
        SignalKind.Note or SignalKind.PolyPressure => $"{NoteNames[number % 12]}{number / 12 - 1}  ·  {number:D3}",
        SignalKind.Control => $"CC {number:D3}",
        SignalKind.Bend => "Pitch Bend",
        SignalKind.ChannelPressure => "Channel Pressure",
        _ => $"Program {number:D3}"
    };
}

internal readonly record struct SignalId(int Channel, SignalKind Kind, int Number);

internal readonly record struct MidiSignal(SignalId Id, int Value, int? ReleaseVelocity = null)
{
    internal static MidiSignal? Decode(uint raw)
    {
        int status = (int)raw & 0xff;
        int channel = (status & 15) + 1;
        int first = (int)(raw >> 8) & 127;
        int second = (int)(raw >> 16) & 127;
        return (status & 0xf0) switch
        {
            0x80 => new(new(channel, SignalKind.Note, first), 0, second),
            0x90 => new(new(channel, SignalKind.Note, first), second, second == 0 ? 0 : null),
            0xa0 => new(new(channel, SignalKind.PolyPressure, first), second),
            0xb0 => new(new(channel, SignalKind.Control, first), second),
            0xc0 => new(new(channel, SignalKind.Program, first), first),
            0xd0 => new(new(channel, SignalKind.ChannelPressure, 0), first),
            0xe0 => new(new(channel, SignalKind.Bend, 0), first | (second << 7)),
            _ => null
        };
    }
}

internal sealed record MidiPort(uint Index, string Name, string Key)
{
    public override string ToString() => Name;
}

internal enum PianoInputAction { None, Reserved, Learn, EnterFull, LeaveFull }

internal readonly record struct MidiPacket(uint Data, long Order, PianoInputAction PianoAction);

internal sealed class MidiInput : IDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const uint InputData = 0x03C3;
    private const uint InputMoreData = 0x03CC;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Caps
    {
        internal ushort Manufacturer, Product;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        internal uint Support;
    }

    private delegate void Callback(IntPtr handle, uint message, UIntPtr instance, UIntPtr data, UIntPtr timestamp);

    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCaps(UIntPtr index, out Caps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out IntPtr handle, uint index, Callback callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint midiInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint midiInClose(IntPtr handle);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetErrorText(uint error, StringBuilder text, uint length);

    private readonly Callback callback;
    private IntPtr handle;
    private static long nextOrder;
    internal readonly ConcurrentQueue<MidiPacket> Messages = new();
    internal Func<uint, PianoInputAction>? AudioInput { get; set; }

    internal MidiInput() => callback = OnMessage;

    internal static List<MidiPort> GetPorts()
    {
        var ports = new List<MidiPort>();
        uint count = midiInGetNumDevs();
        for (uint index = 0; index < count; index++)
        {
            if (midiInGetDevCaps((UIntPtr)index, out var caps, (uint)Marshal.SizeOf<Caps>()) != 0)
                continue;
            string prefix = $"{caps.Manufacturer}/{caps.Product}/{caps.Name}";
            int occurrence = ports.Count(p => p.Key.StartsWith(prefix + "|", StringComparison.Ordinal)) + 1;
            string displayName = occurrence == 1 ? caps.Name : $"{caps.Name} ({occurrence})";
            ports.Add(new(index, displayName, $"{prefix}|{occurrence}"));
        }
        return ports;
    }

    internal void Open(MidiPort port)
    {
        Close();
        Check(midiInOpen(out handle, port.Index, callback, UIntPtr.Zero, CallbackFunction));
        uint result = midiInStart(handle);
        if (result != 0)
        {
            Close();
            Check(result);
        }
    }

    private void OnMessage(IntPtr device, uint message, UIntPtr instance, UIntPtr data, UIntPtr timestamp)
    {
        if (message is InputData or InputMoreData)
            Queue((uint)data.ToUInt64());
    }

    internal void Queue(uint message)
    {
        long order = Interlocked.Increment(ref nextOrder);
        var pianoAction = AudioInput?.Invoke(message) ?? PianoInputAction.None;
        Messages.Enqueue(new(message, order, pianoAction));
    }

    private static void Check(uint error)
    {
        if (error == 0) return;
        var text = new StringBuilder(256);
        midiInGetErrorText(error, text, (uint)text.Capacity);
        throw new InvalidOperationException($"MIDI 输入错误 {error} · {text}");
    }

    internal void Close()
    {
        if (handle != IntPtr.Zero)
        {
            midiInStop(handle);
            midiInClose(handle);
            handle = IntPtr.Zero;
        }
        Messages.Clear();
    }

    public void Dispose() => Close();
}

internal sealed class SignalRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal SignalId Id { get; }
    internal int Value { get; private set; }
    internal bool Seen { get; private set; }
    internal Rule? MappingRule { get; private set; }
    public string Label => Signals.Label(Id.Kind, Id.Number);
    public string DisplayValue => Seen ? Value.ToString() : "—";
    public string DisplayLastVelocity => lastVelocity?.ToString() ?? "—";
    public double Level => Seen ? 100d * Value / Signals.Maximum(Id.Kind) : 0;
    public string Mapping => MappingRule?.Summary ?? "未换键";
    public string EditLabel => MappingRule is null ? "＋ 换键" : "编辑";
    public bool Hot { get; private set; }
    private long lastEvent;
    private int? lastVelocity;

    internal SignalRow(SignalId id)
    {
        Id = id;
        Value = Signals.Rest(id.Kind);
    }
    internal void Record(int value, long now)
    {
        if (Id.Kind == SignalKind.Note && value > 0 && lastVelocity != value)
        {
            lastVelocity = value;
            Notify(nameof(DisplayLastVelocity));
        }
        if (!Seen || Value != value)
        {
            Seen = true;
            Value = value;
            Notify(nameof(DisplayValue));
            Notify(nameof(Level));
        }
        lastEvent = now;
        SetHot(true);
    }
    internal bool Cool(long now)
    {
        if (now - lastEvent < 360) return false;
        SetHot(false);
        return true;
    }
    private void SetHot(bool value)
    {
        if (Hot == value) return;
        Hot = value;
        Notify(nameof(Hot));
    }
    internal void SetRule(Rule? rule)
    {
        MappingRule = rule;
        Notify(nameof(Mapping));
        Notify(nameof(EditLabel));
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

internal sealed record VelocityLayer(int Minimum, int Maximum, KeyMacro Macro)
{
    internal bool Includes(int velocity) => Minimum <= velocity && velocity <= Maximum;
    internal bool IsValid => Minimum >= 1 && Maximum <= 127 && Minimum <= Maximum &&
        Macro is not null && Macro.IsValid(SignalKind.Note);

    internal static int FirstOverlap(IReadOnlyList<VelocityLayer> sorted)
    {
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].Minimum <= sorted[i - 1].Maximum) return i;
        return -1;
    }
}

internal sealed record Rule(string Port, int Channel, SignalKind Kind, int Number, KeyMacro? Macro, VelocityLayer[]? VelocityLayers = null)
{
    internal SignalId Signal => new(Channel, Kind, Number);
    internal bool IsValid => !string.IsNullOrEmpty(Port) && Channel is >= 1 and <= 16 &&
        Enum.IsDefined(typeof(SignalKind), Kind) && Number >= 0 && Number < Signals.Count(Kind) &&
        (VelocityLayers is null ? Macro is not null && Macro.IsValid(Kind) :
            Kind == SignalKind.Note && VelocityLayers.Length > 0 &&
            VelocityLayers.All(layer => layer is not null && layer.IsValid) &&
            VelocityLayer.FirstOverlap(VelocityLayers.OrderBy(layer => layer.Minimum).ToArray()) < 0);

    internal IEnumerable<VelocityLayer> Layers => VelocityLayers ??
        (Macro is null ? Array.Empty<VelocityLayer>() : new[] { new VelocityLayer(1, 127, Macro) });

    internal string Summary => VelocityLayers is null ? Macro?.Summary ?? "" :
        string.Join("  ·  ", VelocityLayers.Select(layer => $"{layer.Minimum}–{layer.Maximum}  {layer.Macro.Summary}"));

    internal KeyMacro? ActionFor(MidiSignal signal, int previous)
    {
        if (VelocityLayers is null) return Macro is KeyMacro macro && macro.Triggers(signal, previous) ? macro : null;
        foreach (var layer in VelocityLayers)
            if (layer.Includes(signal.Value)) return layer.Macro;
        return null;
    }
}

