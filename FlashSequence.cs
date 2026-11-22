namespace MidiKey;

internal sealed class FlashSequence
{
    private static readonly double[] ProgressStops = { 0, 13, 36, 36, 59, 72, 72, 90, 96, 99.7 };
    private static readonly string[] Commands =
    {
        "Opening the local program image",
        "Reading the preset header",
        "Resolving input port aliases",
        "Enumerating MIDI channels",
        "Collecting note identifiers",
        "Decoding controller assignments",
        "Loading velocity layers",
        "Normalizing threshold ranges",
        "Separating tap and hold routes",
        "Indexing virtual key codes",
        "Building the modifier table",
        "Checking chord boundaries",
        "Pausing at the input boundary",
        "Waiting for the next routing frame",
        "Aligning event offsets",
        "Preparing note-on handlers",
        "Preparing note-off handlers",
        "Linking held-key release paths",
        "Queuing timed key sequences",
        "Checking repeat intervals",
        "Baking the lookup table",
        "Joining USB and control routes",
        "Validating release ordering",
        "Reviewing duplicate key targets",
        "Staging the output bindings",
        "Holding the commit window",
        "Waiting for a stable state",
        "Checking the staged rule count",
        "Walking the event graph",
        "Resolving empty layers",
        "Calculating the profile checksum",
        "Comparing the local image",
        "Verifying the action table",
        "Preparing the profile switch",
        "Rechecking the device selection",
        "Finalizing the prepared routes",
        "Syncing program metadata",
        "Preparing the local commit",
        "Verifying the commit boundary",
        "Ready to activate the program"
    };

    private readonly double[] timeStops;
    internal TimeSpan Duration { get; }

    internal FlashSequence(Random random)
    {
        Duration = TimeSpan.FromMilliseconds(random.Next(5300, 14501));
        double firstStall = 0.25 + 0.045 * random.NextDouble();
        double secondStall = 0.57 + 0.055 * random.NextDouble();
        timeStops = new[] { 0, 0.10 + 0.04 * random.NextDouble(), firstStall, firstStall + 0.10,
            0.49, secondStall, secondStall + 0.115, 0.85, 0.93, 1 };
    }

    internal (double Progress, string Command) Sample(TimeSpan elapsed)
    {
        double time = Math.Clamp(elapsed.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
        int segment = 0;
        while (segment < timeStops.Length - 2 && time > timeStops[segment + 1]) segment++;
        double blend = (time - timeStops[segment]) / (timeStops[segment + 1] - timeStops[segment]);
        double eased = blend * blend * (3 - 2 * blend);
        double progress = ProgressStops[segment] + (ProgressStops[segment + 1] - ProgressStops[segment]) * eased;
        int message = Math.Min(Commands.Length - 1, (int)(time * Commands.Length));
        return (progress, Commands[message]);
    }
}
