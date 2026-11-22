using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MidiKey;

public partial class MappingWindow : Window
{
    private sealed class Draft
    {
        internal List<int> Keys { get; }
        internal int Repeat = 1, RepeatGap = 100, NextGap = 150;
        internal Draft() => Keys = new();
        internal Draft(MacroStep step)
        {
            Keys = step.Keys.ToList();
            Repeat = step.Repeat;
            RepeatGap = step.RepeatGap;
            NextGap = step.NextGap;
        }
        internal MacroStep ToStep() => new(Keys.ToArray(), Repeat, RepeatGap, NextGap);
    }

    private sealed class LayerDraft
    {
        internal int Minimum, Maximum;
        internal bool Hold;
        private readonly List<Draft> triggerSteps;
        private List<Draft>? holdSteps;
        internal List<Draft> Steps => Hold ? holdSteps ??= new() { FirstChord() } : triggerSteps;

        internal LayerDraft(int minimum, int maximum, KeyMacro? macro = null)
        {
            Minimum = minimum;
            Maximum = maximum;
            Hold = macro?.Hold == true;
            triggerSteps = EditSteps(macro);
            if (Hold) holdSteps = EditSteps(macro);
        }

        private Draft FirstChord()
        {
            var chord = new Draft();
            chord.Keys.AddRange(triggerSteps[0].Keys);
            return chord;
        }

        internal VelocityLayer ToLayer() => new(Minimum, Maximum,
            new(Steps.Select(step => Hold ? new MacroStep(step.Keys.ToArray(), 1, 0, 0) : step.ToStep()).ToArray(), 1, false, Hold));
    }

    private static List<Draft> EditSteps(KeyMacro? macro) =>
        macro?.Steps.Select(step => new Draft(step)).ToList() ?? new() { new() };

    private readonly SignalId id;
    private readonly string port;
    private readonly SignalKind kind;
    private readonly List<LayerDraft> layers = new();
    private List<Draft> drafts;
    private readonly Dictionary<int, Button> caps = new();
    private int current = -1;
    private int activeLayer;
    private bool changingSelection, changingLayer, ready, extras;
    internal Rule? Result { get; private set; }
    private bool Holding => kind == SignalKind.Note && layers[activeLayer].Hold;

    internal MappingWindow(SignalRow row, string port)
    {
        id = row.Id;
        this.port = port;
        kind = id.Kind;
        var mapping = row.MappingRule;
        drafts = EditSteps(mapping?.Macro);
        InitializeComponent();
        App.ThemeWindow(this);
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        SignalDetails.Text = $"通道 {id.Channel:D2}  ·  {Signals.Names[(int)kind]}  ·  {row.Label}";
        RemoveMapping.Visibility = mapping is null ? Visibility.Collapsed : Visibility.Visible;
        if (kind is not (SignalKind.Note or SignalKind.Program)) ThresholdControls.Visibility = Visibility.Visible;
        Direction.SelectedIndex = mapping?.Macro?.Falling == true ? 1 : 0;
        ThresholdBox.Text = (mapping?.Macro?.Threshold ?? Signals.Threshold(kind)).ToString();
        BuildKeyboard();
        if (kind == SignalKind.Note)
        {
            MinHeight = Math.Min(640, MaxHeight);
            Height = Math.Min(684, MaxHeight);
            VelocityPanel.Visibility = Visibility.Visible;
            VelocityPanel.DataContext = row;
            KeyModeControls.Visibility = Visibility.Visible;
            if (mapping is not null)
                layers.AddRange(mapping.Layers.Select(layer => new LayerDraft(layer.Minimum, layer.Maximum, layer.Macro)));
            if (layers.Count == 0) layers.Add(new(1, 127));
            ShowLayer(0);
        }
        else Select(0);
        ready = true;
    }

    private void RefreshLayers()
    {
        changingLayer = true;
        LayerPicker.ItemsSource = layers.Select(layer => $"力度 {layer.Minimum}–{layer.Maximum}  ·  {(layer.Hold ? "长按" : "触发")}").ToArray();
        LayerPicker.SelectedIndex = activeLayer;
        RemoveLayerButton.IsEnabled = layers.Count > 1;
        changingLayer = false;
    }

    private void ShowLayer(int index)
    {
        activeLayer = index;
        var layer = layers[index];
        drafts = layer.Steps;
        changingLayer = true;
        KeyMode.SelectedIndex = layer.Hold ? 1 : 0;
        VelocityMin.Text = layer.Minimum.ToString();
        VelocityMax.Text = layer.Maximum.ToString();
        RefreshLayers();
        StepActions.Visibility = TimingControls.Visibility = layer.Hold ? Visibility.Collapsed : Visibility.Visible;
        StepHelp.Text = layer.Hold ? "按下琴键时按住所选电脑键，松开时释放。可选多个键一起按住。" :
            "同一步选择多个键可组成组合键。";
        Select(0);
    }

    private void ChangeKeyMode(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingLayer || KeyMode.SelectedIndex < 0) return;
        bool hold = KeyMode.SelectedIndex == 1;
        if (hold == Holding) return;
        if (!CommitLayer())
        {
            changingLayer = true;
            KeyMode.SelectedIndex = Holding ? 1 : 0;
            changingLayer = false;
            return;
        }
        layers[activeLayer].Hold = hold;
        ShowLayer(activeLayer);
    }

    private void PreviewRange(object sender, TextChangedEventArgs e)
    {
        if (!ready || changingLayer || !int.TryParse(VelocityMin.Text, out int minimum) ||
            !int.TryParse(VelocityMax.Text, out int maximum) || minimum < 1 || maximum > 127 || minimum > maximum)
            return;
        layers[activeLayer].Minimum = minimum;
        layers[activeLayer].Maximum = maximum;
        RefreshLayers();
    }

    private bool CommitLayer()
    {
        if (!Commit() || !Number(VelocityMin, 1, 127, "最低力度", out int minimum) ||
            !Number(VelocityMax, minimum, 127, "最高力度", out int maximum)) return false;
        layers[activeLayer].Minimum = minimum;
        layers[activeLayer].Maximum = maximum;
        ErrorText.Text = "";
        return true;
    }

    private void SelectLayer(object sender, SelectionChangedEventArgs e)
    {
        if (changingLayer || LayerPicker.SelectedIndex < 0) return;
        int wanted = LayerPicker.SelectedIndex;
        if (CommitLayer()) ShowLayer(wanted);
        else RefreshLayers();
    }

    private void AddLayer(object sender, RoutedEventArgs e)
    {
        if (!CommitLayer()) return;
        var currentLayer = layers[activeLayer];
        var used = new bool[128];
        used[0] = true;
        foreach (var layer in layers)
            Array.Fill(used, true, layer.Minimum, layer.Maximum - layer.Minimum + 1);
        int minimum = currentLayer.Maximum < 127 ? Array.IndexOf(used, false, currentLayer.Maximum + 1) : -1;
        if (minimum < 0) minimum = Array.IndexOf(used, false);

        int maximum;
        if (minimum < 0)
        {
            if (currentLayer.Minimum == currentLayer.Maximum)
            {
                ErrorText.Text = "所有力度已分配。请先缩小一档的范围。";
                return;
            }
            maximum = currentLayer.Maximum;
            currentLayer.Maximum = (currentLayer.Minimum + maximum) / 2;
            minimum = currentLayer.Maximum + 1;
        }
        else
        {
            int nextUsed = Array.IndexOf(used, true, minimum);
            maximum = nextUsed < 0 ? 127 : nextUsed - 1;
        }

        var added = new LayerDraft(minimum, maximum);
        layers.Add(added);
        layers.Sort((left, right) => left.Minimum.CompareTo(right.Minimum));
        ShowLayer(layers.IndexOf(added));
    }

    private void RemoveLayer(object sender, RoutedEventArgs e)
    {
        if (layers.Count <= 1) return;
        layers.RemoveAt(activeLayer);
        ErrorText.Text = "";
        ShowLayer(Math.Min(activeLayer, layers.Count - 1));
    }

    private void BuildKeyboard()
    {
        var main = new Canvas { Height = 218, Width = 660 };
        for (int row = 0; row < KeyCatalog.Main.Length; row++)
        {
            double left = 0;
            foreach (var cap in KeyCatalog.Main[row])
            {
                if (row == 0 && (cap.Id == 0x70 || cap.Id == 0x74 || cap.Id == 0x78)) left += 23;
                Place(main, cap, left, row * 36, 44 * cap.Units - 4, 32);
                left += 44 * cap.Units;
            }
        }
        var navigation = new Canvas { Height = 218, Width = 129, Margin = new(5, 0, 0, 0) };
        for (int row = 0; row < KeyCatalog.Navigation.Length; row++)
        for (int col = 0; col < KeyCatalog.Navigation[row].Length; col++)
            if (KeyCatalog.Navigation[row][col] is KeyCap cap)
                Place(navigation, cap, col * 43, row * 36, 39, 32);

        var numberPad = new Canvas { Height = 218, Width = 176, Margin = new(8, 0, 0, 0) };
        for (int row = 0; row < KeyCatalog.NumberPad.Length; row++)
        {
            double left = 0;
            foreach (var possible in KeyCatalog.NumberPad[row])
            {
                if (possible is KeyCap cap)
                    Place(numberPad, cap, left, row * 36, cap.Units * 44 - 4,
                        cap.Id is 0x6b or KeyCatalog.NumberEnter ? 68 : 32);
                left += (possible?.Units ?? 1) * 44;
            }
        }
        MainKeys.Children.Add(main);
        NavKeys.Children.Add(navigation);
        NumKeys.Children.Add(numberPad);
        foreach (var key in KeyCatalog.Extra)
        {
            var button = NewKey(key);
            button.Padding = new(12, 5, 12, 5);
            button.Height = 32;
            button.Margin = new(2);
            ExtraKeys.Children.Add(button);
        }
    }

    private void Place(Canvas keyboard, KeyCap key, double x, double y, double width, double height)
    {
        var button = NewKey(key);
        button.Width = width;
        button.Height = height;
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, y);
        keyboard.Children.Add(button);
    }

    private Button NewKey(KeyCap key)
    {
        var button = new Button { Content = key.Label, ToolTip = key.Title, FontSize = 12, Padding = new(1),
            FontFamily = new("Segoe UI, Microsoft YaHei UI") };
        System.Windows.Automation.AutomationProperties.SetName(button, key.Title);
        button.Click += (_, _) => ToggleKey(key.Id);
        caps.Add(key.Id, button);
        return button;
    }

    private void ToggleKey(int id)
    {
        var keys = drafts[current].Keys;
        if (!keys.Remove(id))
        {
            if (keys.Count >= 12) { ErrorText.Text = "每步最多可选择 12 个键。"; return; }
            keys.Add(id);
        }
        ErrorText.Text = "";
        RebuildStepList(current);
        PaintKeys();
    }

    private void ToggleKeyboard(object sender, RoutedEventArgs e)
    {
        extras = !extras;
        FullKeyboard.Visibility = extras ? Visibility.Collapsed : Visibility.Visible;
        ExtraKeys.Visibility = extras ? Visibility.Visible : Visibility.Collapsed;
        KeyboardToggle.Content = extras ? "标准键盘" : "扩展按键";
    }

    private void RebuildStepList(int selection)
    {
        changingSelection = true;
        Steps.ItemsSource = drafts.Select((draft, index) =>
        {
            var step = draft.ToStep();
            string label = step.Keys.Length == 0 ? "选择按键" : step.Summary;
            string gap = index < drafts.Count - 1 ? $"  ·  等待 {step.NextGap} ms" : "";
            return $"{index + 1:D2}   {label}{gap}";
        }).ToArray();
        Steps.SelectedIndex = selection;
        Steps.ScrollIntoView(Steps.SelectedItem);
        changingSelection = false;
    }

    private void PaintKeys()
    {
        var keys = drafts[current].Keys;
        foreach (var (id, button) in caps)
        {
            bool selected = keys.Contains(id);
            button.Background = (Brush)FindResource(selected ? "Accent" : "ButtonInset");
            button.Foreground = (Brush)FindResource(selected ? "AccentInk" : "Accent");
            button.BorderBrush = (Brush)FindResource(selected ? "Accent" : "ControlEdge");
        }
    }

    private void Select(int index)
    {
        current = index;
        var draft = drafts[index];
        RepeatBox.Text = draft.Repeat.ToString();
        RepeatGapBox.Text = draft.RepeatGap.ToString();
        NextGapBox.Text = draft.NextGap.ToString();
        NextGapBox.IsEnabled = index < drafts.Count - 1;
        StepTitle.Text = $"步骤 {index + 1}";
        RebuildStepList(index);
        PaintKeys();
    }

    private bool Commit()
    {
        if (Holding) return true;
        if (!Number(RepeatBox, 1, 100, "连按次数", out int repeat) ||
            !Number(RepeatGapBox, 0, 10000, "连按间隔", out int repeatGap) ||
            !Number(NextGapBox, 0, 10000, "步骤间隔", out int nextGap)) return false;
        var draft = drafts[current];
        draft.Repeat = repeat;
        draft.RepeatGap = repeatGap;
        draft.NextGap = nextGap;
        ErrorText.Text = "";
        return true;
    }

    private bool Number(TextBox box, int minimum, int maximum, string label, out int result)
    {
        if (int.TryParse(box.Text, out result) && result >= minimum && result <= maximum) return true;
        ErrorText.Text = $"{label}请输入 {minimum}–{maximum} 的整数。";
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void SelectStep(object sender, SelectionChangedEventArgs e)
    {
        if (changingSelection || Steps.SelectedIndex < 0) return;
        int wanted = Steps.SelectedIndex;
        if (Commit()) Select(wanted);
        else RebuildStepList(current);
    }

    private void AddStep(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        if (drafts.Count >= 32) { ErrorText.Text = "最多可添加 32 个步骤。"; return; }
        drafts.Insert(current + 1, new());
        Select(current + 1);
    }

    private void RemoveStep(object sender, RoutedEventArgs e)
    {
        drafts.RemoveAt(current);
        if (drafts.Count == 0) drafts.Add(new());
        ErrorText.Text = "";
        Select(Math.Min(current, drafts.Count - 1));
    }

    private void MoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int offset)
    {
        int destination = current + offset;
        if (destination < 0 || destination >= drafts.Count || !Commit()) return;
        (drafts[current], drafts[destination]) = (drafts[destination], drafts[current]);
        Select(destination);
    }

    private void ChangeDirection(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || kind != SignalKind.Bend) return;
        if (Direction.SelectedIndex == 1 && ThresholdBox.Text == "8193") ThresholdBox.Text = "8191";
        else if (Direction.SelectedIndex == 0 && ThresholdBox.Text == "8191") ThresholdBox.Text = "8193";
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (kind == SignalKind.Note)
        {
            SaveNote();
            return;
        }
        if (!Commit() || !CheckSteps()) return;
        bool falling = Direction.SelectedIndex == 1;
        int threshold = Signals.Threshold(kind);
        if (ThresholdControls.Visibility == Visibility.Visible &&
            !Number(ThresholdBox, falling ? 0 : 1, Signals.Maximum(kind) - (falling ? 1 : 0), "触发阈值", out threshold))
            return;
        var macro = new KeyMacro(drafts.Select(draft => draft.ToStep()).ToArray(), threshold, falling);
        Result = new(port, id.Channel, id.Kind, id.Number, macro);
        DialogResult = true;
    }

    private bool CheckSteps()
    {
        int empty = drafts.FindIndex(draft => draft.Keys.Count == 0);
        if (empty < 0) return true;
        Select(empty);
        ErrorText.Text = $"请为步骤 {empty + 1} 选择至少一个按键。";
        return false;
    }

    private void SaveNote()
    {
        if (!CommitLayer()) return;
        int empty = layers.FindIndex(layer => layer.Steps.Any(step => step.Keys.Count == 0));
        if (empty >= 0)
        {
            ShowLayer(empty);
            CheckSteps();
            return;
        }
        var sorted = layers.OrderBy(layer => layer.Minimum).ToArray();
        var result = sorted.Select(layer => layer.ToLayer()).ToArray();
        int overlap = VelocityLayer.FirstOverlap(result);
        if (overlap >= 0)
        {
            var layer = sorted[overlap];
            var previous = sorted[overlap - 1];
            ShowLayer(layers.IndexOf(layer));
            ErrorText.Text = $"力度 {layer.Minimum}–{layer.Maximum} 与 {previous.Minimum}–{previous.Maximum} 重叠，请调整范围。";
            return;
        }
        Result = new(port, id.Channel, id.Kind, id.Number, null, result);
        DialogResult = true;
    }

    private void Delete(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }
}
