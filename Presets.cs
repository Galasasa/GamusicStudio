using System.IO;
using System.Text.Json;

namespace MidiKey;

internal sealed record Preset(Guid Id, string Name, string? Port, Rule[] Rules, string? Artwork = null,
    PianoSettings? Piano = null, MouseSpeedBinding? MouseSpeed = null, MouseMotionBinding? MouseMotion = null);

internal static class GamePresets
{
    internal sealed record Game(Guid Id, string Name, string Artwork, string EnglishName);
    private static readonly Game[] Games =
    {
        new(new("706a4ccd-f0ca-4ee6-ae91-bede336181ad"), "无畏契约", "valorant", "VALORANT"),
        new(new("771f7ad3-f8d0-4be5-860a-b325bf9d0e95"), "逃离塔科夫", "tarkov", "Escape from Tarkov"),
        new(new("17b53ae8-6400-4422-8c07-4f9f35223f0f"), "三角洲行动", "delta", "Delta Force"),
        new(new("f1ed7f08-e7a8-4a10-9cc2-73deb93a1b34"), "泰坦陨落", "titanfall", "Titanfall")
    };

    internal static Game? FindArtwork(string? artwork) => Games.FirstOrDefault(game => game.Artwork == artwork);

    internal static Preset[] AddMissing(Preset[] existing)
    {
        var toAdd = new List<Preset>();
        var presets = existing.ToArray();
        foreach (var game in Games)
        {
            int index = Array.FindIndex(presets, preset => preset.Id == game.Id);
            if (index < 0) index = Array.FindIndex(presets, preset => preset.Name == game.Name);
            if (index >= 0)
                presets[index] = presets[index] with { Artwork = game.Artwork };
            else if (!presets.Any(preset => preset.Artwork == game.Artwork))
                toAdd.Add(new(game.Id, game.Name, null, Array.Empty<Rule>(), game.Artwork));
        }
        return toAdd.Concat(presets).ToArray();
    }
}

internal sealed class SettingsStore
{
    internal const int MaxNameLength = 60;
    private const string DefaultName = "默认夺舍程序";
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiKey");
    private static readonly string FileName = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private sealed record LegacySettings(string? Port, Rule[]? Rules);

    public Preset[] Presets { get; init; } = Array.Empty<Preset>();
    public Guid? ActivePresetId { get; init; } = Guid.Empty;
    internal Preset? Active => ActivePresetId is Guid id ? Find(id) : null;
    internal Preset Find(Guid id) => Presets.First(preset => preset.Id == id);

    internal static SettingsStore Default(string? port = null, Rule[]? rules = null)
    {
        var preset = new Preset(Guid.NewGuid(), DefaultName, port, ValidRules(rules));
        return new() { Presets = GamePresets.AddMissing(new[] { preset }), ActivePresetId = preset.Id };
    }

    internal static SettingsStore Read()
    {
        if (!File.Exists(FileName)) return Default();
        string json = File.ReadAllText(FileName);
        var saved = JsonSerializer.Deserialize<SettingsStore>(json) ??
            throw new InvalidDataException("配置文件内容为空");
        var presets = (saved.Presets ?? Array.Empty<Preset>())
            .Where(preset => preset is not null && preset.Id != Guid.Empty && !string.IsNullOrWhiteSpace(preset.Name))
            .GroupBy(preset => preset.Id)
            .Select(group => group.Last())
            .Select(preset => preset with
            {
                Name = preset.Name.Trim(),
                Rules = ValidRules(preset.Rules),
                Piano = preset.Piano?.Normalize(),
                MouseSpeed = preset.MouseSpeed is { IsValid: true } binding ? binding : null,
                MouseMotion = preset.MouseMotion is { IsValid: true } motion ? motion : null
            })
            .ToArray();
        if (presets.Length == 0)
        {
            var legacy = JsonSerializer.Deserialize<LegacySettings>(json);
            return Default(legacy?.Port, legacy?.Rules);
        }
        int legacyDefault = Array.FindIndex(presets, preset => preset.Name == "默认预设");
        if (legacyDefault >= 0 && !presets.Any(preset => preset.Name == DefaultName))
            presets[legacyDefault] = presets[legacyDefault] with { Name = DefaultName };
        Guid? active = saved.ActivePresetId;
        if (active is Guid id && !presets.Any(preset => preset.Id == id)) active = presets[0].Id;
        return new() { Presets = GamePresets.AddMissing(presets), ActivePresetId = active };
    }

    private static Rule[] ValidRules(Rule[]? rules) => (rules ?? Array.Empty<Rule>())
        .Where(rule => rule is not null && rule.IsValid)
        .GroupBy(rule => (rule.Port, rule.Signal)).Select(group => group.Last()).ToArray();

    internal void Write()
    {
        Directory.CreateDirectory(Folder);
        string temporary = Path.Combine(Folder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, WriteOptions));
            File.Move(temporary, FileName, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
