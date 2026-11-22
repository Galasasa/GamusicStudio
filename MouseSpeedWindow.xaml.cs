using System.Windows;
using System.Windows.Threading;

namespace MidiKey;

public partial class MouseSpeedWindow : Window
{
    private const string SpeedHint = "拖动滑块可直接调整 Windows 鼠标灵敏度。";
    private readonly Action<int> applySpeed;
    private MouseSpeedBinding? selection;
    private bool learning;
    private bool syncingSpeed = true;
    internal bool IsLearning => learning;
    internal MouseSpeedBinding? Result { get; private set; }

    internal MouseSpeedWindow(MouseSpeedBinding? current, IReadOnlyList<MidiPort> ports, Action<int> applySpeed)
    {
        this.applySpeed = applySpeed;
        InitializeComponent();
        App.ThemeWindow(this);
        selection = current;
        MinimumSpeed.ItemsSource = MaximumSpeed.ItemsSource = Enumerable.Range(1, 20).ToArray();
        MinimumSpeed.SelectedItem = current?.Minimum ?? 1;
        MaximumSpeed.SelectedItem = current?.Maximum ?? 20;
        SpeedStatus.Text = SpeedHint;
        ShowCurrentSpeed(WindowsMouseSpeed.Read());
        var speedMonitor = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            (_, _) => ShowCurrentSpeed(WindowsMouseSpeed.Read()), Dispatcher);
        Closed += (_, _) => speedMonitor.Stop();
        LearnButton.IsEnabled = ports.Count > 0;
        BindingStatus.Text = ports.Count > 0 ? "支持发送 MIDI CC 或 Pitch Bend 的推子和旋钮。"
            : "连接 MIDI 设备后即可学习控件。";
        var device = ports.FirstOrDefault(port => port.Key == current?.Port);
        ShowSelection(device?.Name);
    }

    internal void ShowCurrentSpeed(int? speed)
    {
        if (!speed.HasValue) SpeedStatus.Text = "暂时无法读取 Windows 鼠标灵敏度。";
        else if (!SpeedSlider.IsEnabled) SpeedStatus.Text = SpeedHint;
        syncingSpeed = true;
        SpeedSlider.IsEnabled = speed.HasValue;
        if (speed is int value) SpeedSlider.Value = value;
        CurrentSpeed.Text = speed?.ToString() ?? "—";
        syncingSpeed = false;
    }

    internal void ShowSpeedWriteError(int? speed)
    {
        ShowCurrentSpeed(speed);
        SpeedStatus.Text = "无法调整 Windows 鼠标灵敏度。";
    }

    private void ChangeSpeed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (syncingSpeed) return;
        SpeedStatus.Text = SpeedHint;
        applySpeed((int)Math.Round(e.NewValue));
    }

    internal void Observe(MidiPort port, MidiSignal signal, PianoModeKey? modeKey, MouseMotionBinding? motion)
    {
        if (!learning || !MouseSpeedBinding.Supports(signal.Id.Kind, signal.Id.Number)) return;
        if (modeKey is not null && modeKey.Kind == signal.Id.Kind && modeKey.Port == port.Key && modeKey.Number == signal.Id.Number &&
            (modeKey.Channel == 0 || modeKey.Channel == signal.Id.Channel))
        {
            BindingStatus.Text = "此控件已用于切换钢琴模式，请操作另一个控件。";
            return;
        }
        if (motion?.Matches(port.Key, signal.Id) == true)
        {
            BindingStatus.Text = "此控件已用于鼠标横移，请操作另一个控件。";
            return;
        }
        selection = new(port.Key, signal.Id.Channel, signal.Id.Number, Kind: signal.Id.Kind);
        learning = false;
        BindingStatus.Text = "已识别控件。保存后，移动控件即可调整鼠标灵敏度。";
        ShowSelection(port.Name);
    }

    private void ShowSelection(string? device)
    {
        BindingLabel.Text = selection is { } binding ? $"{device ?? "设备未连接"} · CH {binding.Channel:D2} · {binding.Label}" : "未绑定";
        BindingLabel.ToolTip = BindingLabel.Text;
        LearnButton.Content = learning ? "停止学习" : "学习 MIDI 控件";
        SaveButton.IsEnabled = !learning && selection is not null;
        RemoveButton.IsEnabled = selection is not null;
    }

    private void ToggleLearning(object sender, RoutedEventArgs e)
    {
        learning = !learning;
        LearnButton.Content = learning ? "停止学习" : "学习 MIDI 控件";
        SaveButton.IsEnabled = !learning && selection is not null;
        BindingStatus.Text = learning ? "请移动要绑定的推子或旋钮。" : "已停止学习。";
    }

    private void SaveBinding(object sender, RoutedEventArgs e)
    {
        if (selection is not { } binding || MinimumSpeed.SelectedItem is not int low || MaximumSpeed.SelectedItem is not int high) return;
        if (low > high)
        {
            BindingStatus.Text = "最低灵敏度不能高于最高灵敏度。";
            return;
        }
        Result = binding with { Minimum = low, Maximum = high };
        DialogResult = true;
    }

    private void RemoveBinding(object sender, RoutedEventArgs e) => DialogResult = true;
}
