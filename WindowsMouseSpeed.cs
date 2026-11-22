using System.Runtime.InteropServices;

namespace MidiKey;

internal sealed record MouseSpeedBinding(string Port, int Channel, int Controller, int Minimum = 1, int Maximum = 20,
    SignalKind Kind = SignalKind.Control)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Port) &&
        Channel is >= 1 and <= 16 && Supports(Kind, Controller) &&
        Minimum is >= 1 and <= 20 && Maximum >= Minimum && Maximum <= 20;

    internal static bool Supports(SignalKind kind, int number) => kind switch
    {
        SignalKind.Control => number is >= 0 and <= 119,
        SignalKind.Bend => number == 0,
        _ => false
    };

    internal string Label => Signals.Label(Kind, Controller);

    internal bool Matches(string? port, SignalId signal) => port == Port &&
        signal.Kind == Kind && signal.Channel == Channel && signal.Number == Controller;

    internal int SpeedFor(int value)
    {
        int limit = Signals.Maximum(Kind);
        return Minimum + (value * (Maximum - Minimum) + limit / 2) / limit;
    }
}

internal static class WindowsMouseSpeed
{
    private const uint GetMouseSpeed = 0x0070;
    private const uint SetMouseSpeed = 0x0071;
    private const uint UpdateUserProfile = 0x0001;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadParameter(uint action, uint parameter, out int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteParameter(uint action, uint parameter, IntPtr value, uint flags);

    internal static int? Read() => ReadParameter(GetMouseSpeed, 0, out int value, 0) ? value : null;

    internal static bool Apply(int speed) => WriteParameter(SetMouseSpeed, 0, new IntPtr(speed), UpdateUserProfile);
}
