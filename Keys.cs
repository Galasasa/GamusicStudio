using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace MidiKey;

internal sealed record KeyCap(int Id, string Label, string Title, double Units = 1)
{
    internal bool Modifier => Id is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
    internal bool Extended => Id is 0xA3 or 0xA5 or 0x2C or 0x6F or >= 0x21 and <= 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or >= 0xA6 and <= 0xB7 or KeyCatalog.NumberEnter;
}

internal static class KeyCatalog
{
    internal const int NumberEnter = 0x10d;
    private static KeyCap K(int id, string label, double units = 1, string? title = null) =>
        new(id, label, title ?? label, units);
    private static KeyCap[] Letters(string letters) => letters.Select(letter => K(letter, letter.ToString())).ToArray();
    internal static readonly KeyCap[][] Main =
    {
        new[] { K(0x1b,"Esc") }.Concat(Enumerable.Range(1,12).Select(i => K(0x6f+i,$"F{i}"))).ToArray(),
        new[] { K(0xc0,"` ~") }.Concat("1234567890".Select(d => K(d,d.ToString())))
            .Concat(new[] { K(0xbd,"- _"), K(0xbb,"= +"), K(0x08,"Backspace",2) }).ToArray(),
        new[] { K(0x09,"Tab",1.5) }.Concat(Letters("QWERTYUIOP"))
            .Concat(new[] { K(0xdb,"[ {"), K(0xdd,"] }"), K(0xdc,"\\ |",1.5) }).ToArray(),
        new[] { K(0x14,"Caps",1.75) }.Concat(Letters("ASDFGHJKL"))
            .Concat(new[] { K(0xba,"; :"), K(0xde,"' \""), K(0x0d,"Enter",2.25) }).ToArray(),
        new[] { K(0xa0,"Shift",2.25,"左 Shift") }.Concat(Letters("ZXCVBNM"))
            .Concat(new[] { K(0xbc,", <"), K(0xbe,". >"), K(0xbf,"/ ?"), K(0xa1,"Shift",2.75,"右 Shift") }).ToArray(),
        new[] { K(0xa2,"Ctrl",1.25,"左 Ctrl"), K(0x5b,"Win",1.25,"左 Win"), K(0xa4,"Alt",1.25,"左 Alt"),
            K(0x20,"Space",6.25), K(0xa5,"Alt",1.25,"右 Alt"), K(0x5c,"Win",1.25,"右 Win"),
            K(0x5d,"Menu",1.25), K(0xa3,"Ctrl",1.25,"右 Ctrl") }
    };
    internal static readonly KeyCap?[][] Navigation =
    {
        new[] { K(0x2c,"PrtSc"), K(0x91,"ScrLk"), K(0x13,"Pause") },
        new[] { K(0x2d,"Ins"), K(0x24,"Home"), K(0x21,"PgUp") },
        new[] { K(0x2e,"Del"), K(0x23,"End"), K(0x22,"PgDn") },
        new KeyCap?[3],
        new KeyCap?[] { null, K(0x26,"↑"), null },
        new[] { K(0x25,"←"), K(0x28,"↓"), K(0x27,"→") }
    };
    internal static readonly KeyCap?[][] NumberPad =
    {
        new KeyCap?[4],
        new[] { K(0x90,"Num"), K(0x6f,"/",title:"小键盘 /"), K(0x6a,"*",title:"小键盘 *"), K(0x6d,"-",title:"小键盘 -") },
        new[] { K(0x67,"7",title:"小键盘 7"), K(0x68,"8",title:"小键盘 8"), K(0x69,"9",title:"小键盘 9"), K(0x6b,"+",title:"小键盘 +") },
        new KeyCap?[] { K(0x64,"4",title:"小键盘 4"), K(0x65,"5",title:"小键盘 5"), K(0x66,"6",title:"小键盘 6"), null },
        new[] { K(0x61,"1",title:"小键盘 1"), K(0x62,"2",title:"小键盘 2"), K(0x63,"3",title:"小键盘 3"), K(NumberEnter,"Enter",title:"小键盘 Enter") },
        new KeyCap?[] { K(0x60,"0",2,"小键盘 0"), K(0x6e,".",title:"小键盘 ."), null }
    };
    internal static readonly KeyCap[] Extra = Enumerable.Range(13,12).Select(i => K(0x6f+i,$"F{i}"))
        .Concat(new[] { K(0xad,"静音"), K(0xae,"音量−"), K(0xaf,"音量＋"), K(0xb0,"下一首"), K(0xb1,"上一首"),
            K(0xb2,"停止"), K(0xb3,"播放/暂停"), K(0xa6,"浏览器返回"), K(0xa7,"浏览器前进"), K(0xaa,"搜索"),
            K(0xac,"浏览器主页"), K(0xb4,"邮件"), K(0x0c,"Clear"), K(0xe2,"ISO \\"),
            K(0x15,"Kana"), K(0x19,"Hanja"), K(0x1c,"转换"), K(0x1d,"不转换") }).ToArray();
    internal static readonly Dictionary<int, KeyCap> All = Main.SelectMany(row => row)
        .Concat(Navigation.Concat(NumberPad).SelectMany(row => row).OfType<KeyCap>())
        .Concat(Extra).ToDictionary(key => key.Id);
}

internal sealed record MacroStep(int[] Keys, int Repeat, int RepeatGap, int NextGap)
{
    internal IEnumerable<int> PressOrder => Keys.OrderByDescending(id => KeyCatalog.All[id].Modifier);
    internal string Chord => string.Join(" + ", PressOrder.Select(id => KeyCatalog.All[id].Title));
    internal string Summary => Repeat > 1 ? $"{Chord} ×{Repeat}" : Chord;
    internal bool IsValid => Keys is { Length: > 0 and <= 12 } && Keys.All(KeyCatalog.All.ContainsKey) &&
        Keys.Distinct().Count() == Keys.Length && Repeat is >= 1 and <= 100 &&
        RepeatGap is >= 0 and <= 10000 && NextGap is >= 0 and <= 10000;
}

internal sealed record KeyMacro(MacroStep[] Steps, int Threshold, bool Falling, bool Hold = false)
{
    internal string Summary => Hold ? $"长按 {Steps[0].Chord}" : string.Join("  →  ", Steps.Select(step => step.Summary));
    internal bool IsValid(SignalKind kind) => Steps is { Length: > 0 and <= 32 } &&
        Steps.All(step => step is not null && step.IsValid) &&
        (!Hold || (kind == SignalKind.Note && Steps is { Length: 1 } && Steps[0].Repeat == 1)) &&
        Threshold >= (Falling ? 0 : 1) && Threshold <= Signals.Maximum(kind) - (Falling ? 1 : 0);

    internal bool Triggers(MidiSignal signal, int previous) => signal.Id.Kind switch
    {
        SignalKind.Note => signal.Value > 0,
        SignalKind.Program => true,
        _ when Falling => previous > Threshold && signal.Value <= Threshold,
        _ => previous < Threshold && signal.Value >= Threshold
    };
}

internal sealed class KeyOutput : IDisposable
{
    private const uint ExtendedKey = 0x0001, KeyUp = 0x0002, ScanCode = 0x0008, VkToExtendedScan = 4;
    private const uint MouseMove = 0x0001;
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public Keyboard Keyboard;
        [FieldOffset(0)] public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Mouse
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Keyboard
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyEx(uint code, uint mode, IntPtr layout);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);

    internal static bool MoveMouseHorizontally(int distance)
    {
        var movement = new Input { Data = new InputData { Mouse = new Mouse { X = distance, Flags = MouseMove } } };
        return SendInput(1, new[] { movement }, Marshal.SizeOf<Input>()) == 1;
    }

    private readonly record struct Job(KeyMacro Macro, IntPtr Target, CancellationToken Cancellation);
    private sealed record HeldNote(KeyMacro Macro, int[] Keys);
    private readonly object keyLock = new();
    private readonly Dictionary<int, int> activeKeys = new();
    private readonly Dictionary<SignalRow, HeldNote> heldNotes = new();
    private IntPtr heldTarget;
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(48)
        { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource current = new();
    private readonly Task worker;
    private readonly Action<string> report;
    private readonly IntPtr keyboardLayout = GetKeyboardLayout(0);

    internal KeyOutput(Action<string> report)
    {
        this.report = report;
        worker = Task.Run(Run);
    }
    private IntPtr TargetWindow()
    {
        IntPtr target = GetForegroundWindow();
        if (target == IntPtr.Zero || GetWindowThreadProcessId(target, out uint process) == 0 ||
            process == Environment.ProcessId)
        {
            report("先切换到要接收电脑按键的窗口");
            return IntPtr.Zero;
        }
        return target;
    }

    internal void Send(KeyMacro macro)
    {
        IntPtr target = TargetWindow();
        if (target == IntPtr.Zero) return;
        if (!queue.Writer.TryWrite(new(macro, target, current.Token)))
            report("输出队列已满，本次触发已跳过");
    }

    internal void Hold(SignalRow row, KeyMacro macro)
    {
        IntPtr target = TargetWindow();
        if (target == IntPtr.Zero) return;
        lock (keyLock)
        {
            if (heldTarget != target) ReleaseHeldNotes();
            if (heldNotes.TryGetValue(row, out var prior))
            {
                if (ReferenceEquals(prior.Macro, macro)) return;
                ReleaseNote(row);
            }
            int[] order = macro.Steps[0].PressOrder.ToArray();
            try
            {
                AcquireKeys(order);
                heldNotes.Add(row, new(macro, order));
                heldTarget = target;
                report($"已发送 · {macro.Summary}");
            }
            catch (InvalidOperationException ex) { report(ex.Message); }
        }
    }

    internal void Release(SignalRow row)
    {
        lock (keyLock) ReleaseNote(row);
    }

    internal void ReleaseOnFocusChange()
    {
        lock (keyLock)
        {
            if (heldNotes.Count > 0 && heldTarget != GetForegroundWindow()) ReleaseHeldNotes();
        }
    }

    private void ReleaseNote(SignalRow row)
    {
        if (heldNotes.Remove(row, out var held)) ReleaseKeys(held.Keys);
        if (heldNotes.Count == 0) heldTarget = IntPtr.Zero;
    }

    private void ReleaseHeldNotes()
    {
        foreach (var held in heldNotes.Values.Reverse()) ReleaseKeys(held.Keys);
        heldNotes.Clear();
        heldTarget = IntPtr.Zero;
    }

    internal void Cancel()
    {
        current.Cancel();
        current.Dispose();
        current = new();
        lock (keyLock) ReleaseHeldNotes();
    }
    private async Task Run()
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                try
                {
                    if (job.Cancellation.IsCancellationRequested) continue;
                    await Play(job);
                    if (!job.Cancellation.IsCancellationRequested) report($"已发送 · {job.Macro.Summary}");
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException ex) { report(ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Play(Job job)
    {
        for (int i = 0; i < job.Macro.Steps.Length; i++)
        {
            var step = job.Macro.Steps[i];
            var order = step.PressOrder.ToArray();
            for (int repeat = 0; repeat < step.Repeat; repeat++)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != job.Target)
                    throw new InvalidOperationException("前台窗口已切换，当前宏已结束");
                await Press(order, job.Cancellation);
                if (repeat + 1 < step.Repeat && step.RepeatGap > 0)
                    await Task.Delay(step.RepeatGap, job.Cancellation);
            }
            if (i + 1 < job.Macro.Steps.Length && step.NextGap > 0)
                await Task.Delay(step.NextGap, job.Cancellation);
        }
    }
    private async Task Press(int[] keys, CancellationToken cancellation)
    {
        lock (keyLock)
        {
            cancellation.ThrowIfCancellationRequested();
            AcquireKeys(keys);
        }
        try { await Task.Delay(24, cancellation); }
        finally
        {
            lock (keyLock) ReleaseKeys(keys);
        }
    }

    private void AcquireKeys(int[] keys)
    {
        int[] needed = keys.Where(key => !activeKeys.ContainsKey(key)).ToArray();
        if (needed.Length > 0)
        {
            Input[] down = needed.Select(id => KeyEvent(id, false)).ToArray();
            int count = (int)SendInput((uint)down.Length, down, Marshal.SizeOf<Input>());
            if (count != down.Length)
            {
                SendReleases(needed.Take(count).Reverse());
                throw new InvalidOperationException("Windows 拒绝了部分按键输入，请检查目标窗口的权限");
            }
        }
        foreach (int key in keys) activeKeys[key] = activeKeys.GetValueOrDefault(key) + 1;
    }

    private void ReleaseKeys(int[] keys)
    {
        var released = new List<int>();
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            int key = keys[i];
            int count = activeKeys[key];
            if (count > 1) activeKeys[key] = count - 1;
            else
            {
                activeKeys.Remove(key);
                released.Add(key);
            }
        }
        SendReleases(released);
    }

    private void SendReleases(IEnumerable<int> keys)
    {
        Input[] up = keys.Select(id => KeyEvent(id, true)).ToArray();
        if (up.Length == 0) return;
        int count = (int)SendInput((uint)up.Length, up, Marshal.SizeOf<Input>());
        for (int i = count; i < up.Length; i++)
            SendInput(1, new[] { up[i] }, Marshal.SizeOf<Input>());
    }
    private Input KeyEvent(int id, bool release)
    {
        var cap = KeyCatalog.All[id];
        ushort virtualKey = (ushort)(id == KeyCatalog.NumberEnter ? 0x0d : id);
        uint scan = MapVirtualKeyEx(virtualKey, VkToExtendedScan, keyboardLayout);
        uint prefix = scan >> 8;
        // Preserve the logical Windows behavior of keypad digits and special OS keys.
        bool useScan = scan != 0 && prefix is 0 or 0xe0 &&
            id is not (>= 0x60 and <= 0x69 or >= 0xa6 and <= 0xb7 or 0x13 or 0x2c or 0x15 or 0x19 or 0x1c or 0x1d);
        uint flags = (cap.Extended || (useScan && prefix == 0xe0) ? ExtendedKey : 0) | (release ? KeyUp : 0);
        var keyboard = useScan
            ? new Keyboard { ScanCode = (ushort)(scan & 0xff), Flags = flags | ScanCode }
            : new Keyboard { VirtualKey = virtualKey, Flags = flags };
        return new Input { Type = 1, Data = new InputData { Keyboard = keyboard } };
    }
    public void Dispose()
    {
        current.Cancel();
        lock (keyLock) ReleaseHeldNotes();
        lifetime.Cancel();
        queue.Writer.TryComplete();
        worker.GetAwaiter().GetResult();
        current.Dispose();
        lifetime.Dispose();
    }
}
