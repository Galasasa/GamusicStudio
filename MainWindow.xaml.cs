using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MidiKey;

public partial class MainWindow : Window
{
    private const int SignalsPerPage = 8;
    private sealed record FeedItem(string Label, string Detail, string Value);
    private sealed class InputSource
    {
        internal readonly MidiInput Midi = new();
        internal readonly Dictionary<SignalId, SignalRow> Rows = new();
        internal readonly Dictionary<(int Channel, SignalKind Kind), SignalRow[]> Groups = new();
        internal readonly Dictionary<(int Channel, SignalKind Kind), SignalRow> LastInputs = new();
        internal readonly int?[] ChannelValues = new int?[16];
        internal readonly long[] ChannelActivity = new long[16];
        internal MidiPort? Port;
        internal string? Error;
        internal bool ReceivedInput;
        internal long LastSystemLog;

        internal void Connect(MidiPort? port, IEnumerable<Rule> savedRules)
        {
            Port = null;
            Error = null;
            ReceivedInput = false;
            LastSystemLog = 0;
            LastInputs.Clear();
            Array.Clear(ChannelValues);
            Array.Clear(ChannelActivity);
            if (port is not null)
            {
                try
                {
                    Midi.Open(port);
                    Port = port;
                }
                catch (InvalidOperationException ex) { Error = ex.Message; }
            }
            MakeRows(savedRules);
        }

        private void MakeRows(IEnumerable<Rule> savedRules)
        {
            Rows.Clear();
            Groups.Clear();
            var mappings = savedRules.Where(rule => rule.Port == Port?.Key).ToDictionary(rule => rule.Signal);
            for (int ch = 1; ch <= 16; ch++)
            {
                foreach (SignalKind kind in Enum.GetValues<SignalKind>())
                {
                    var group = new SignalRow[Signals.Count(kind)];
                    for (int number = 0; number < group.Length; number++)
                    {
                        SignalId id = new(ch, kind, number);
                        var row = new SignalRow(id);
                        if (mappings.TryGetValue(id, out var rule)) row.SetRule(rule);
                        group[number] = Rows[id] = row;
                    }
                    Groups[(ch, kind)] = group;
                }
            }
        }
    }

    private readonly InputSource primary = new();
    private readonly InputSource daw = new();
    private InputSource viewedSource;
    private readonly KeyOutput keys;
    private readonly DispatcherTimer monitor;
    private readonly DispatcherTimer rescan;
    private readonly ObservableCollection<FeedItem> entries = new();
    private readonly HashSet<SignalRow> hotRows = new();
    private readonly Button[] channelButtons = new Button[16];
    private readonly TextBlock[] channelValues = new TextBlock[16];
    private readonly List<Button> pageButtons = new();
    private SignalRow[] filteredRows = Array.Empty<SignalRow>();
    private SignalRow[] pageRows = Array.Empty<SignalRow>();
    private int pageIndex;
    private int PageCount => (filteredRows.Length + SignalsPerPage - 1) / SignalsPerPage;
    private List<MidiPort> ports = new();
    private SettingsStore settings;
    private int channel = 1;
    private bool ready, updatingDevices, followingInput, editing, output = true;
    private volatile bool closing;

    public MainWindow()
    {
        viewedSource = primary;
        primary.Midi.AudioInput = raw => pianoSound.Enqueue(raw, primary.Port?.Key);
        daw.Midi.AudioInput = raw => pianoSound.Enqueue(raw, daw.Port?.Key);
        InitializeComponent();
        PreviewMouseMove += (_, e) => ParticleOverlay.Move(e.GetPosition(ParticleOverlay));
        MouseLeave += (_, _) => ParticleOverlay.Leave();
        PresetViewport.Resources["PresetArtHeight"] = 208d;
        App.ThemeWindow(this);
        keys = new KeyOutput(message =>
        {
            if (!closing) Dispatcher.BeginInvoke(new Action(() => OutputStatus.Text = message));
        });
        try { settings = SettingsStore.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            settings = SettingsStore.Default();
            PresetStatus.Text = "无法读取旧配置 · " + ex.Message;
        }
        primary.Connect(null, CurrentPreset.Rules);
        MakeChannels();
        MakePageButtons();
        Feed.ItemsSource = entries;
        SourcePicker.ItemsSource = new[] { "USB MIDI", "DAW 控件" };
        SourcePicker.SelectedIndex = 0;
        KindPicker.ItemsSource = Signals.Names;
        KindPicker.SelectedIndex = 0;
        FilterPicker.ItemsSource = new[] { "全部信号", "已收到", "已换键" };
        FilterPicker.SelectedIndex = 0;
        InitializePianoControls();
        ready = true;
        UpdateTable();
        UpdateOutputControls();
        UpdatePresetList();
        monitor = new(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => PollInputs(), Dispatcher);
        rescan = new(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Discover(false), Dispatcher);
        Loaded += (_, _) => Discover(true);
    }

    private void MakeChannels()
    {
        for (int i = 0; i < channelButtons.Length; i++)
        {
            int number = i + 1;
            var label = new TextBlock { Text = $"CH {number:D2}", FontFamily = new("Consolas"),
                FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var value = new TextBlock { Text = "—", FontFamily = new("Consolas"), FontSize = 12,
                Foreground = Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 2, 0, 0) };
            var stack = new StackPanel();
            stack.Children.Add(label);
            stack.Children.Add(value);
            var button = new Button { Content = stack, Height = 46, Margin = new(2), Padding = new(2, 2, 2, 2),
                ToolTip = $"通道 {number:D2} · 显示该通道最近收到的值" };
            System.Windows.Automation.AutomationProperties.SetName(button, $"通道 {number:D2}");
            button.Click += (_, _) =>
            {
                Follow.IsChecked = false;
                channel = number;
                pageIndex = 0;
                UpdateTable();
                UpdatePianoMappings();
            };
            Channels.Children.Add(button);
            channelButtons[i] = button;
            channelValues[i] = value;
        }
        PaintChannels(Environment.TickCount64);
    }

    private void MakePageButtons()
    {
        int largestGroup = primary.Groups.Values.Max(group => group.Length);
        int count = (largestGroup + SignalsPerPage - 1) / SignalsPerPage;
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = $"{index + 1:D2}",
                Width = 28,
                Height = 28,
                Margin = new(1, 0, 1, 0),
                Padding = new(0),
                FontFamily = new("Consolas")
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"第 {index + 1:D2} 页");
            button.Click += (_, _) => ShowPage(index);
            pageButtons.Add(button);
            PageNumbers.Children.Add(button);
        }
    }

    private Brush Brush(string resource) => (Brush)FindResource(resource);

    private void PaintChannels(long now)
    {
        for (int i = 0; i < 16; i++)
        {
            long activity = viewedSource.ChannelActivity[i];
            bool active = activity != 0 && now - activity < 450;
            channelValues[i].Text = viewedSource.ChannelValues[i]?.ToString() ?? "—";
            channelValues[i].Foreground = Brush(active ? "Accent" : "Muted");
            channelButtons[i].BorderBrush = Brush(i + 1 == channel || active ? "Accent" : "ControlEdge");
        }
    }

    private void Discover(bool force)
    {
        if (editing) return;
        var available = MidiInput.GetPorts();
        if (!force && ports.SequenceEqual(available)) return;
        ports = available;
        var desired = ResolvePort();
        updatingDevices = true;
        DevicePicker.ItemsSource = ports;
        DevicePicker.SelectedItem = desired;
        updatingDevices = false;
        if (force || primary.Port != desired || daw.Port != DawPortFor(desired)) Connect(desired);
    }

    private MidiPort? ResolvePort()
    {
        var preset = CurrentPreset;
        string? preferred = preset.Id == Guid.Empty ? unflashedPort : preset.Port;
        return ports.FirstOrDefault(port => port.Key == preferred) ?? ports.FirstOrDefault();
    }

    private MidiPort? DawPortFor(MidiPort? port) =>
        port is not null && port.Name.StartsWith("Oxygen Pro ", StringComparison.OrdinalIgnoreCase)
            ? ports.FirstOrDefault(candidate => candidate.Name.Equals($"MIDIIN3 ({port.Name})", StringComparison.OrdinalIgnoreCase))
            : null;

    private void Connect(MidiPort? port)
    {
        keys.Cancel();
        primary.Midi.Close();
        daw.Midi.Close();
        primary.Connect(port, CurrentPreset.Rules);
        daw.Connect(DawPortFor(port), CurrentPreset.Rules);
        ConfigurePianoSound(reload: true);
        ConfigurePiano();
        SourcePicker.Visibility = primary.Port is not null && daw.Port is not null ? Visibility.Visible : Visibility.Collapsed;
        SetViewedSource(primary.Port is not null || daw.Port is null ? primary : daw);
        UpdateDeviceStatus();
        entries.Clear();
        FeedPlaceholder.Visibility = Visibility.Visible;
        hotRows.Clear();
        pageIndex = 0;
        UpdateTable();
    }

    private void UpdateDeviceStatus()
    {
        bool connected = primary.Port is not null || daw.Port is not null;
        string activity = primary.ReceivedInput || daw.ReceivedInput ? "已收到 MIDI 输入" : "等待 MIDI 输入";
        DeviceDot.Fill = Brush(connected ? "Accent" : "Muted");
        DeviceStatus.Text = (primary.Port is not null, daw.Port is not null) switch
        {
            (true, true) => $"已监听 USB MIDI 与 DAW 控件 · {activity}",
            (true, false) when daw.Error is not null => $"DAW 控件连接失败 · USB MIDI {activity}",
            (true, false) => $"已连接 · {activity}",
            (false, true) => $"USB MIDI 连接失败 · DAW 控件 {activity}",
            _ => primary.Error ?? "未检测到 MIDI 输入设备"
        };
        DeviceStatus.ToolTip = daw.Port is not null || daw.Error is not null
            ? $"{DescribeSource(primary)}\n{DescribeSource(daw)}"
            : primary.Error;
    }

    private static string DescribeSource(InputSource source) => source.Port is MidiPort port
        ? $"{port.Name} · {(source.ReceivedInput ? "已收到信号" : "等待信号")}"
        : source.Error ?? "未连接";

    private void SetViewedSource(InputSource source)
    {
        viewedSource = source;
        SourcePicker.SelectedIndex = source == daw ? 1 : 0;
    }

    private void SelectSource(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || SourcePicker.SelectedIndex < 0) return;
        var source = SourcePicker.SelectedIndex == 1 ? daw : primary;
        if (source == viewedSource || source.Port is null) return;
        Follow.IsChecked = false;
        viewedSource = source;
        pageIndex = 0;
        UpdateTable();
    }

    private void RefreshDevices(object sender, RoutedEventArgs e) => Discover(true);
    private void SelectDevice(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updatingDevices || DevicePicker.SelectedItem is not MidiPort port) return;
        if (CurrentPreset.Id == Guid.Empty)
        {
            unflashedPort = port.Key;
            Connect(port);
        }
        else if (SavePreset(CurrentPreset with { Port = port.Key })) Connect(port);
        else
        {
            updatingDevices = true;
            DevicePicker.SelectedItem = ResolvePort();
            updatingDevices = false;
        }
    }

    private void ChangeFilter(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || followingInput) return;
        pageIndex = 0;
        UpdateTable();
    }

    private void UpdateTable(SignalRow? target = null)
    {
        if (KindPicker.SelectedIndex < 0) return;
        var group = viewedSource.Groups[(channel, (SignalKind)KindPicker.SelectedIndex)];
        filteredRows = FilterPicker.SelectedIndex switch
        {
            1 => group.Where(row => row.Seen).ToArray(),
            2 => group.Where(row => row.MappingRule is not null).ToArray(),
            _ => group
        };
        int targetIndex = target is null ? -1 : Array.IndexOf(filteredRows, target);
        ShowPage(targetIndex >= 0 ? targetIndex / SignalsPerPage : pageIndex);
        if (targetIndex >= 0 && !ReferenceEquals(SignalTable.SelectedItem, target)) SignalTable.SelectedItem = target;
        EmptyTable.Visibility = filteredRows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelTitle.Text = $"通道 {channel:D2}";
        RuleCount.Text = $"{CurrentPreset.Rules.Count(rule => rule.Port == viewedSource.Port?.Key)} 条换键";
        PaintChannels(Environment.TickCount64);
    }

    private void ShowPage(int index)
    {
        pageIndex = Math.Clamp(index, 0, Math.Max(0, PageCount - 1));
        int offset = pageIndex * SignalsPerPage;
        var items = filteredRows.AsSpan(offset, Math.Min(SignalsPerPage, filteredRows.Length - offset));
        if (!items.SequenceEqual(pageRows))
        {
            var selected = SignalTable.SelectedItem;
            pageRows = items.ToArray();
            SignalTable.ItemsSource = pageRows;
            if (selected is SignalRow row && pageRows.Contains(row)) SignalTable.SelectedItem = row;
        }
        TableCaption.Text = items.Length == 0 ? "0 个信号" : $"{offset + 1}–{offset + items.Length} / {filteredRows.Length}";
        Pager.Visibility = PageCount == 0 ? Visibility.Hidden : Visibility.Visible;
        PreviousPage.IsEnabled = pageIndex > 0;
        NextPage.IsEnabled = pageIndex + 1 < PageCount;
        PaintPages();
    }

    private void PaintPages()
    {
        viewedSource.LastInputs.TryGetValue((channel, (SignalKind)KindPicker.SelectedIndex), out var last);
        for (int i = 0; i < pageButtons.Count; i++)
        {
            var button = pageButtons[i];
            button.Visibility = i < PageCount ? Visibility.Visible : Visibility.Collapsed;
            if (i >= PageCount) continue;

            bool incoming = false;
            int end = Math.Min((i + 1) * SignalsPerPage, filteredRows.Length);
            for (int j = i * SignalsPerPage; j < end && !incoming; j++)
                incoming = ReferenceEquals(filteredRows[j], last) || filteredRows[j].Hot;
            bool selected = i == pageIndex;
            button.Background = Brush(incoming ? "Accent" : "ButtonInset");
            button.Foreground = Brush(incoming ? "AccentInk" : selected ? "Accent" : "Muted");
            button.BorderBrush = Brush(selected || incoming ? "Accent" : "ControlEdge");
            button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            button.ToolTip = $"第 {i + 1:D2} 页" + (selected ? " · 当前页" : "") + (incoming ? " · 收到 MIDI 信号" : "");
        }
    }

    private void GoToPreviousPage(object sender, RoutedEventArgs e) => ShowPage(pageIndex - 1);
    private void GoToNextPage(object sender, RoutedEventArgs e) => ShowPage(pageIndex + 1);

    private void ResizeSignalRows(object sender, SizeChangedEventArgs e)
    {
        double body = e.NewSize.Height - SignalTable.ColumnHeaderHeight - SignalTable.BorderThickness.Top -
            SignalTable.BorderThickness.Bottom;
        SignalTable.RowHeight = Math.Clamp(Math.Floor(body / SignalsPerPage), 21, 46);
    }

    private bool TryTakePacket(out InputSource source, out MidiPacket packet)
    {
        bool hasPrimary = primary.Midi.Messages.TryPeek(out var primaryPacket);
        bool hasDaw = daw.Midi.Messages.TryPeek(out var dawPacket);
        source = !hasDaw || (hasPrimary && primaryPacket.Order < dawPacket.Order) ? primary : daw;
        return source.Midi.Messages.TryDequeue(out packet);
    }

    private void PollInputs()
    {
        keys.ReleaseOnFocusChange();
        long now = Environment.TickCount64;
        ShowPianoSoundError();
        SignalRow? latest = null;
        InputSource latestSource = viewedSource;
        int? lastPianoPitch = null;
        bool pianoChanged = false;
        bool seenChanged = false, receivedInView = false;
        for (int i = 0; i < 512 && TryTakePacket(out var source, out var packet); i++)
        {
            if (MidiSignal.Decode(packet.Data) is not MidiSignal signal)
            {
                LogSystem(source, packet.Data, now);
                continue;
            }
            if (!source.ReceivedInput)
            {
                source.ReceivedInput = true;
                UpdateDeviceStatus();
            }
            var row = source.Rows[signal.Id];
            int previous = row.Value;
            if (source == viewedSource && channel == signal.Id.Channel && KindPicker.SelectedIndex == (int)signal.Id.Kind)
            {
                receivedInView = true;
                seenChanged |= !row.Seen;
            }
            row.Record(signal.Value, now);
            if (signal.Id.Kind == SignalKind.Note)
            {
                if (signal.Value == 0) keys.Release(row);
                if (source == primary)
                {
                    pianoChanged = true;
                    if (signal.Value > 0) lastPianoPitch = signal.Id.Number;
                }
            }
            else if (signal.Id.Kind == SignalKind.Control && signal.Id.Number is 120 or 123)
            {
                ReleaseNoteChannel(source, signal.Id.Channel, now);
                pianoChanged |= source == primary;
            }
            hotRows.Add(row);
            int ch = signal.Id.Channel - 1;
            source.ChannelActivity[ch] = now;
            source.ChannelValues[ch] = signal.Value;
            string detail = signal.ReleaseVelocity is int velocity ? $"松开力度 {velocity}" : Signals.Names[(int)signal.Id.Kind];
            string prefix = source == daw ? "DAW · " : "";
            AddFeed(new(row.Label, $"{prefix}CH {signal.Id.Channel:D2} · {detail}", signal.Value.ToString()));
            if (signal.Id.Kind != SignalKind.Note || signal.Value > 0)
            {
                source.LastInputs[(signal.Id.Channel, signal.Id.Kind)] = row;
                latest = row;
                latestSource = source;
            }
            if (packet.PianoAction != PianoInputAction.None)
            {
                HandlePianoMode(packet.PianoAction, signal, source.Port?.Key);
                continue;
            }
            if (source == primary && signal.Id.Number == pianoSound.Regions.SwitchNote &&
                signal.Id.Kind is SignalKind.Note or SignalKind.PolyPressure) continue;
            if (output && !editing && CanSendMappedKeys && signal.Value != 0 && signal.Id.Kind == SignalKind.Note)
            {
                var macro = row.MappingRule?.ActionFor(signal, previous);
                if (macro?.Hold == true) keys.Hold(row, macro);
                else
                {
                    keys.Release(row);
                    if (macro is not null) keys.Send(macro);
                }
            }
            else if (output && !editing && CanSendMappedKeys && signal.Id.Kind != SignalKind.Note &&
                row.MappingRule?.ActionFor(signal, previous) is KeyMacro macro)
                keys.Send(macro);
        }
        if (lastPianoPitch is int number) ShowPianoPitch(number);
        if (pianoChanged) PaintPianoNotes();
        bool activityCooled = hotRows.Count > 0 && hotRows.RemoveWhere(row => row.Cool(now)) > 0;
        if (latest is not null && Follow.IsChecked == true && !editing && !KindPicker.IsDropDownOpen &&
            !FilterPicker.IsDropDownOpen && !DevicePicker.IsDropDownOpen && !SourcePicker.IsDropDownOpen)
        {
            var id = latest.Id;
            bool changedChannel = channel != id.Channel;
            if (latestSource != viewedSource || channel != id.Channel || KindPicker.SelectedIndex != (int)id.Kind) pageIndex = 0;
            SetViewedSource(latestSource);
            channel = id.Channel;
            followingInput = true;
            KindPicker.SelectedIndex = (int)id.Kind;
            followingInput = false;
            UpdateTable(latest);
            if (changedChannel) UpdatePianoMappings();
        }
        else if (seenChanged && FilterPicker.SelectedIndex == 1) UpdateTable();
        else if (receivedInView || activityCooled) PaintPages();
        PaintChannels(now);
    }

    private void AddFeed(FeedItem entry)
    {
        FeedPlaceholder.Visibility = Visibility.Collapsed;
        entries.Insert(0, entry);
        if (entries.Count > 40) entries.RemoveAt(entries.Count - 1);
    }

    private void LogSystem(InputSource source, uint raw, long now)
    {
        int status = (int)raw & 0xff;
        if (status < 0xf0 || ((status is 0xf8 or 0xfe) && now - source.LastSystemLog < 650)) return;
        source.LastSystemLog = now;
        string name = status switch
        {
            0xf1 => "时间码", 0xf2 => "歌曲位置", 0xf3 => "歌曲选择", 0xf6 => "调音请求",
            0xf8 => "时钟", 0xfa => "开始", 0xfb => "继续", 0xfc => "停止",
            0xfe => "活动感应", 0xff => "重置", _ => "系统消息"
        };
        AddFeed(new(name, source == daw ? "DAW · MIDI 系统" : "MIDI 系统", $"0x{status:X2}"));
    }

    private void ToggleOutput(object sender, RoutedEventArgs e)
    {
        output = !output;
        if (!output) keys.Cancel();
        UpdateOutputControls();
        SetPresetStatus(output ? "按键输出已开启。切换到目标窗口后操作 MIDI 控件。" : "输出已暂停，MIDI 数值继续更新。");
    }

    private void EditMapping(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SignalRow row }) Edit(row);
    }

    private void EditOnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is Visual)
        {
            if (element is ButtonBase) return;
            if (element is DataGridRow { Item: SignalRow row })
            {
                Edit(row);
                e.Handled = true;
                return;
            }
            element = VisualTreeHelper.GetParent(element);
        }
    }

    private void Edit(SignalRow row)
    {
        if (editing) return;
        if (viewedSource.Port is not MidiPort port)
        {
            OutputStatus.Text = "连接 MIDI 输入端口后即可设置换键。";
            return;
        }
        if (!ReferenceEquals(viewedSource.Rows.GetValueOrDefault(row.Id), row)) return;
        editing = true;
        keys.Cancel();
        try
        {
            var editor = new MappingWindow(row, port.Key) { Owner = this };
            if (editor.ShowDialog() != true) return;
            var rules = CurrentPreset.Rules.Where(rule => rule.Port != port.Key || rule.Signal != row.Id).ToList();
            if (editor.Result is Rule mapping) rules.Add(mapping);
            if (SavePreset(CurrentPreset with { Rules = rules.ToArray() }))
            {
                row.SetRule(editor.Result);
                ConfigurePianoSound();
                UpdateTable();
                UpdatePianoMappings();
                OutputStatus.Text = editor.Result is null ? "换键已删除。" : "换键已保存到当前方案。";
            }
        }
        finally { editing = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = true;
        flashCancellation?.Cancel();
        monitor.Stop();
        rescan.Stop();
        primary.Midi.Dispose();
        daw.Midi.Dispose();
        pianoSound.Dispose();
        keys.Dispose();
        base.OnClosed(e);
    }
}
