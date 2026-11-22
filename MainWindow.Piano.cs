using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MidiKey;

public partial class MainWindow
{
    private sealed record PianoKey(int Number, bool Black, Border Frame, TextBlock Legend);
    private sealed record PianoAreaChoice(PianoArea Area, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly int[] PianoSizes = { 25, 32, 37, 49, 54, 61, 73, 76, 88 };
    private static readonly Regex PianoModelSize = new(@"(?<!\d)(25|32|37|49|54|61|73|76|88)(?!\d)", RegexOptions.CultureInvariant);
    private readonly List<PianoKey> pianoKeys = new();
    private readonly PianoOutput pianoSound = new();
    private int pianoStart, pianoCount;
    private int? selectedPianoNote;
    private bool configuringPiano, configuringPianoSound, draggingSustain;
    private bool fullKeyboardPiano, learningPianoKey;

    private void ConfigurePianoSound(bool reload = false)
    {
        var preset = CurrentPreset;
        var settings = preset.Piano ?? PianoSettings.Default;
        if (pianoSound.Configure(PianoRegions.ForPreset(settings, primary.Port?.Key, preset.Rules), primary.Port?.Key, reload))
        {
            if (fullKeyboardPiano) keys.Cancel();
            fullKeyboardPiano = learningPianoKey = false;
            UpdateOutputControls();
        }
        RefreshPianoControls();
        ShowPianoSoundError();
    }

    private void InitializePianoControls()
    {
        var octaves = Enumerable.Range(-PianoSettings.OctaveLimit, PianoSettings.OctaveLimit * 2 + 1)
            .Select(value => value == 0 ? "原调" : $"{(value > 0 ? "+" : "−")}{Math.Abs(value)} 八度").ToArray();
        PianoAreaOctave.ItemsSource = octaves;
    }

    private void RefreshPianoControls()
    {
        var regions = pianoSound.Regions;
        var settings = regions.Settings;
        int lastVisible = pianoStart + pianoCount - 1;
        var choices = pianoKeys.Count == 0 ? Array.Empty<PianoAreaChoice>() : regions.Areas
            .Where(area => area.End >= pianoStart && area.Start <= lastVisible)
            .Select(area => new PianoAreaChoice(area, PianoAreaLabel(area, lastVisible))).ToArray();
        var choice = choices.FirstOrDefault(item => selectedPianoNote is int note && item.Area.Contains(note)) ??
            choices.FirstOrDefault(item => item.Area.Enabled) ?? choices.FirstOrDefault();
        selectedPianoNote = choice is null ? null : Math.Max(pianoStart, choice.Area.Start);
        configuringPianoSound = true;
        PianoAreaPicker.ItemsSource = choices;
        PianoAreaPicker.SelectedItem = choice;
        PianoAreaPicker.IsEnabled = choices.Length > 0 && !fullKeyboardPiano;
        SetPianoAreaControls(choice);
        KickSoundToggle.IsChecked = settings.KickEnabled;
        PianoSustain.Value = settings.Sustain;
        SustainValue.Text = $"{settings.Sustain}%";
        KickSoundToggle.IsEnabled = PianoSustain.IsEnabled = regions.HasSound || fullKeyboardPiano;
        configuringPianoSound = false;
        RefreshPianoModeControls();
    }

    private string PianoAreaLabel(PianoArea area, int lastVisible)
    {
        string range = area.Start == 0 && area.End == 127 ? "全键盘" :
            $"{PianoNoteName(Math.Max(pianoStart, area.Start))}–{PianoNoteName(Math.Min(lastVisible, area.End))}";
        return $"{range} · {(area.Enabled ? "开" : "关")}";
    }

    private void SetPianoAreaControls(PianoAreaChoice? choice)
    {
        PianoAreaEnabled.IsEnabled = choice is not null && !fullKeyboardPiano;
        PianoAreaEnabled.IsChecked = choice?.Area.Enabled == true;
        PianoAreaOctave.IsEnabled = choice?.Area.Enabled == true && !fullKeyboardPiano;
        PianoAreaOctave.SelectedIndex = choice is null ? -1 : choice.Area.Octaves + PianoSettings.OctaveLimit;
    }

    private void SelectPianoArea(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || configuringPianoSound) return;
        var choice = PianoAreaPicker.SelectedItem as PianoAreaChoice;
        selectedPianoNote = choice is null ? null : Math.Max(pianoStart, choice.Area.Start);
        configuringPianoSound = true;
        SetPianoAreaControls(choice);
        configuringPianoSound = false;
        PaintPianoNotes();
    }

    private void ChangePianoArea(object sender, RoutedEventArgs e)
    {
        if (!ready || configuringPianoSound || primary.Port is not MidiPort port ||
            PianoAreaPicker.SelectedItem is not PianoAreaChoice choice || PianoAreaOctave.SelectedIndex < 0) return;
        var changed = choice.Area with
        {
            Enabled = PianoAreaEnabled.IsChecked == true,
            Octaves = PianoAreaOctave.SelectedIndex - PianoSettings.OctaveLimit
        };
        if (changed == choice.Area) return;
        var settings = CurrentPreset.Piano ?? PianoSettings.Default;
        SavePianoSettings(settings.SetArea(port.Key, changed));
    }

    private void PianoSoundChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || configuringPianoSound) return;
        var settings = CurrentPreset.Piano ?? PianoSettings.Default;
        var changed = settings with
        {
            KickEnabled = KickSoundToggle.IsChecked == true,
            Sustain = (int)PianoSustain.Value
        };
        if (changed != settings) SavePianoSettings(changed);
    }

    private void SavePianoSettings(PianoSettings settings)
    {
        SavePreset(CurrentPreset with { Piano = settings });
        ConfigurePianoSound();
        UpdatePianoMappings();
    }

    private void SelectPianoModeKey(object sender, RoutedEventArgs e)
    {
        if (primary.Port is null) return;
        learningPianoKey = !learningPianoKey;
        if (learningPianoKey) keys.Cancel();
        pianoSound.SetLearning(learningPianoKey);
        RefreshPianoModeControls();
        UpdatePianoMappings();
    }

    private void StopLearningPianoModeKey()
    {
        if (!learningPianoKey) return;
        learningPianoKey = false;
        pianoSound.SetLearning(false);
        RefreshPianoModeControls();
        UpdatePianoMappings();
    }

    private void SavePianoModeKey(int note, SignalKind kind = SignalKind.Note, int channel = 0, string? sourcePort = null)
    {
        if (!learningPianoKey || primary.Port is not MidiPort port) return;
        learningPianoKey = false;
        var settings = CurrentPreset.Piano ?? PianoSettings.Default;
        SavePianoSettings(settings with { ModeKey = new(sourcePort ?? port.Key, note, kind, channel) });
        pianoSound.SetLearning(false);
    }

    private void RemovePianoModeKey(object sender, RoutedEventArgs e)
    {
        StopLearningPianoModeKey();
        if (CurrentPreset.Piano is { ModeKey: not null } settings)
            SavePianoSettings(settings with { ModeKey = null });
    }

    private void RefreshPianoModeControls()
    {
        var key = pianoSound.Regions.Settings.ModeKey;
        PianoModeKeyButton.IsEnabled = primary.Port is not null;
        PianoModeKeyButton.Content = learningPianoKey ? "取消选键" :
            key is not null ? $"切换键 · {(key.Kind == SignalKind.Note ? PianoNoteName(key.Number) : $"CC {key.Number}")}" : "设置钢琴切换键";
        ClearPianoModeKey.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
        PianoModeStatus.Visibility = learningPianoKey || key is not null ? Visibility.Visible : Visibility.Collapsed;
        PianoModeStatus.Text = learningPianoKey ? "按琴键、按钮或推动控件" :
            fullKeyboardPiano ? "全键盘钢琴" : "换键与空区";
    }

    private void HandlePianoMode(PianoInputAction action, MidiSignal signal, string? port)
    {
        if (action == PianoInputAction.Learn)
        {
            if (CurrentPreset.MouseSpeed?.Matches(port, signal.Id) == true ||
                CurrentPreset.MouseMotion?.Matches(port, signal.Id) == true)
            {
                StopLearningPianoModeKey();
                OutputStatus.Text = "此控件已有鼠标绑定，请选择另一个切换键。";
                return;
            }
            SavePianoModeKey(signal.Id.Number, signal.Id.Kind, signal.Id.Channel, port);
            return;
        }
        if (!(action is PianoInputAction.EnterFull or PianoInputAction.LeaveFull)) return;
        keys.Cancel();
        fullKeyboardPiano = action == PianoInputAction.EnterFull;
        UpdateOutputControls();
        RefreshPianoControls();
        UpdatePianoMappings();
    }

    private void SustainDragStarted(object sender, DragStartedEventArgs e) => draggingSustain = true;

    private void SustainDragCompleted(object sender, DragCompletedEventArgs e)
    {
        draggingSustain = false;
        PianoSoundChanged(sender, e);
    }

    private void SustainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || configuringPianoSound) return;
        int value = (int)e.NewValue;
        SustainValue.Text = $"{value}%";
        if (draggingSustain) pianoSound.PreviewSustain(value);
        else PianoSoundChanged(sender, e);
    }

    private void ShowPianoSoundError()
    {
        string? error = pianoSound.Error;
        if (PianoSoundError.Text == (error ?? "")) return;
        PianoSoundError.Text = error ?? "";
        PianoSoundError.ToolTip = error;
        PianoSoundError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConfigurePiano()
    {
        PianoSizePicker.Visibility = Visibility.Collapsed;
        PianoBody.Visibility = primary.Port is null ? Visibility.Collapsed : Visibility.Visible;
        if (primary.Port is not MidiPort port)
        {
            pianoKeys.Clear();
            PianoBody.Children.Clear();
            PianoTitle.Text = "钢琴键 · 未连接";
            PianoRange.Text = "";
            RefreshPianoControls();
            return;
        }

        var model = PianoModelSize.Match(port.Name);
        int count = model.Success ? int.Parse(model.Value) : 61;
        if (!model.Success)
        {
            configuringPiano = true;
            PianoSizePicker.ItemsSource = PianoSizes.Select(size => $"{size} 键").ToArray();
            PianoSizePicker.SelectedIndex = Array.IndexOf(PianoSizes, count);
            PianoSizePicker.Visibility = Visibility.Visible;
            configuringPiano = false;
        }
        SetPianoSize(count);
    }

    private void SelectPianoSize(object sender, SelectionChangedEventArgs e)
    {
        if (!configuringPiano && PianoSizePicker.SelectedIndex >= 0)
            SetPianoSize(PianoSizes[PianoSizePicker.SelectedIndex]);
    }

    private void SetPianoSize(int count)
    {
        pianoCount = count;
        pianoStart = count switch
        {
            25 or 37 => 48,
            32 => 41,
            73 or 76 => 28,
            88 => 21,
            _ => 36
        };
        PianoTitle.Text = PianoSizePicker.Visibility == Visibility.Visible ? "钢琴键" : $"钢琴键 · {count} 键";
        BuildPiano();
    }

    private static bool IsBlackKey(int note) => note % 12 is 1 or 3 or 6 or 8 or 10;
    private static string PianoNoteName(int number) => Signals.Label(SignalKind.Note, number).Split(' ')[0];

    private void BuildPiano()
    {
        pianoKeys.Clear();
        PianoBody.Children.Clear();
        PianoRange.Text = $"{PianoNoteName(pianoStart)}–{PianoNoteName(pianoStart + pianoCount - 1)}";
        for (int number = pianoStart; number < pianoStart + pianoCount; number++)
        {
            bool black = IsBlackKey(number);
            var legend = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new("Consolas, Microsoft YaHei UI"),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new(1, 0, 1, black ? 4 : 5),
                IsHitTestVisible = false
            };
            var frame = new Border
            {
                BorderThickness = new(1),
                CornerRadius = new(0, 0, 3, 3),
                Tag = number,
                Child = legend
            };
            frame.MouseLeftButtonDown += SelectPianoAreaFromKey;
            Panel.SetZIndex(frame, black ? 1 : 0);
            pianoKeys.Add(new(number, black, frame, legend));
            PianoBody.Children.Add(frame);
        }
        LayoutPiano();
        RefreshPianoControls();
        UpdatePianoMappings();
    }

    private void SelectPianoAreaFromKey(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int note }) return;
        if (learningPianoKey)
        {
            SavePianoModeKey(note);
            return;
        }
        if (fullKeyboardPiano || pianoSound.Regions.Find(note) is null) return;
        PianoAreaPicker.SelectedItem = PianoAreaPicker.Items.OfType<PianoAreaChoice>()
            .FirstOrDefault(choice => choice.Area.Contains(note));
    }

    private void ResizePiano(object sender, SizeChangedEventArgs e) => LayoutPiano();

    private void LayoutPiano()
    {
        if (pianoKeys.Count == 0 || PianoBody.ActualWidth <= 0) return;
        const double blackWidth = 0.61;
        double leftInset = pianoKeys[0].Black ? blackWidth / 2 : 0;
        double rightInset = pianoKeys[^1].Black ? blackWidth / 2 : 0;
        double unit = PianoBody.ActualWidth / (pianoKeys.Count(key => !key.Black) + leftInset + rightInset);
        int whites = 0;
        foreach (var key in pianoKeys)
        {
            double position = leftInset + whites;
            Canvas.SetLeft(key.Frame, (position - (key.Black ? blackWidth / 2 : 0)) * unit);
            key.Frame.Width = Math.Max(1, unit * (key.Black ? blackWidth : 1) - (key.Black ? 0 : 1));
            key.Frame.Height = PianoBody.Height * (key.Black ? 0.61 : 1);
            if (!key.Black) whites++;
        }
    }

    private void ShowPianoPitch(int number)
    {
        if (pianoKeys.Count == 0 || (number >= pianoStart && number < pianoStart + pianoCount)) return;
        int octaves = number < pianoStart ? (int)Math.Floor((number - pianoStart) / 12d) :
            (int)Math.Ceiling((number - (pianoStart + pianoCount - 1)) / 12d);
        pianoStart = Math.Clamp(pianoStart + 12 * octaves, 0, 128 - pianoCount);
        BuildPiano();
    }

    private void PaintPianoNotes()
    {
        var selectedArea = (PianoAreaPicker.SelectedItem as PianoAreaChoice)?.Area;
        foreach (var key in pianoKeys)
        {
            bool pressed = false;
            for (int ch = 1; ch <= 16 && !pressed; ch++)
                pressed = primary.Rows[new(ch, SignalKind.Note, key.Number)].Value > 0;
            bool sounding = pianoSound.Regions.Pitch(key.Number, fullKeyboardPiano) is not null;
            bool switchKey = pianoSound.Regions.SwitchNote == key.Number;
            bool selected = !fullKeyboardPiano && selectedArea?.Contains(key.Number) == true;
            key.Frame.Background = Brush(pressed ? "Accent" :
                sounding ? key.Black ? "SignalFill" : "PianoEnabled" : key.Black ? "Background" : "SelectionFill");
            key.Frame.BorderBrush = Brush(pressed ? "Accent" : switchKey ? "Magenta" :
                selected ? "PianoAreaEdge" : key.Black ? "ControlEdge" : "Line");
            key.Frame.Effect = pressed ? (System.Windows.Media.Effects.Effect)FindResource("CyanGlow") : null;
            key.Legend.Foreground = Brush(pressed ? "AccentInk" : key.Legend.Tag is true ? "Accent" : "Muted");
        }
    }

    private void UpdatePianoMappings()
    {
        foreach (var key in pianoKeys)
        {
            var mappings = new List<Rule>();
            for (int ch = 1; ch <= 16; ch++)
                if (primary.Rows[new(ch, SignalKind.Note, key.Number)].MappingRule is Rule candidate)
                    mappings.Add(candidate);
            var rule = mappings.FirstOrDefault(candidate => candidate.Channel == channel) ?? mappings.FirstOrDefault();
            bool switchKey = pianoSound.Regions.SwitchNote == key.Number;
            bool showMapping = rule is not null && !fullKeyboardPiano && !switchKey;
            key.Legend.Tag = switchKey || showMapping;
            key.Legend.Text = switchKey ? "切" : showMapping ? PianoMappingLabel(rule!) :
                key.Number % 12 == 0 ? PianoNoteName(key.Number) : "";
            var area = pianoSound.Regions.Find(key.Number);
            key.Frame.Cursor = learningPianoKey || (!fullKeyboardPiano && area is not null) ? Cursors.Hand : Cursors.Arrow;
            string pianoDetail = switchKey ? "\n全键盘钢琴切换键" : fullKeyboardPiano ? "\n钢琴 · 原调" :
                area is null ? "" : "\n点击选择空区 · " + (area.Value.Enabled
                    ? pianoSound.Regions.Pitch(key.Number) is int pitch ? $"发声 {PianoNoteName(pitch)}" : "音高超出 MIDI 范围"
                    : "钢琴弹奏已关闭");
            key.Frame.ToolTip = Signals.Label(SignalKind.Note, key.Number) + pianoDetail + (showMapping ? string.Concat(
                mappings.Select(mapping => $"\nCH {mapping.Channel:D2} · {mapping.Summary}")) : "");
            System.Windows.Automation.AutomationProperties.SetName(key.Frame,
                switchKey ? $"{PianoNoteName(key.Number)} 钢琴切换键" :
                showMapping ? $"{PianoNoteName(key.Number)} 换键 {key.Legend.Text}" : PianoNoteName(key.Number));
        }
        PaintPianoNotes();
    }

    private static string PianoMappingLabel(Rule rule) => string.Join("/", rule.Layers
        .SelectMany(layer => layer.Macro.Steps)
        .Select(step => string.Join("+", step.PressOrder.Select(key => KeyCatalog.All[key].Label)))
        .Distinct());

    private void FitWorkspaceWindow()
    {
        MinHeight = Math.Min(740, SystemParameters.WorkArea.Height);
        if (WindowState == WindowState.Normal && Height < MinHeight) Height = MinHeight;
    }

    private void ReleaseNoteChannel(InputSource source, int channel, long now)
    {
        foreach (var row in source.Groups[(channel, SignalKind.Note)])
        {
            keys.Release(row);
            if (row.Value <= 0) continue;
            row.Record(0, now);
            hotRows.Add(row);
        }
    }
}
