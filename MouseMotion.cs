namespace MidiKey;

internal enum MouseMotionMode
{
    Automatic,
    Absolute,
    Mackie
}

internal sealed record MouseMotionBinding(string Port, int Channel, int Controller, int Speed, MouseMotionMode Mode)
{
    internal const int MaxSpeed = 48;
    internal bool IsValid => !string.IsNullOrWhiteSpace(Port) && Channel is >= 1 and <= 16 &&
        Controller is >= 0 and <= 119 && Speed is >= 1 and <= MaxSpeed && Enum.IsDefined(Mode);

    internal bool Matches(string? port, SignalId signal) => port == Port &&
        signal.Kind == SignalKind.Control && signal.Channel == Channel && signal.Number == Controller;

    internal int HorizontalDistance(int value, int? previous, bool dawPort)
    {
        bool relative = Mode == MouseMotionMode.Mackie ||
            (Mode == MouseMotionMode.Automatic && dawPort && Channel == 1 && Controller is >= 16 and <= 23);
        int steps = relative ? ((value & 0x40) == 0 ? 1 : -1) * (value & 0x3f) : value - (previous ?? value);
        return steps * Speed;
    }
}
