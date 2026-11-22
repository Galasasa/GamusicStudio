using System.Windows;

namespace MidiKey;

public partial class MouseMotionWindow : Window
{
    private MouseMotionBinding? selection;
    private bool learning;
    internal MouseMotionBinding? Result { get; private set; }

    internal MouseMotionWindow(Preset preset, IReadOnlyList<MidiPort> ports)
    {
        InitializeComponent();
        App.ThemeWindow(this);
        selection = preset.MouseMotion;
        ModePicker.SelectedIndex = (int)(selection?.Mode ?? MouseMotionMode.Automatic);
        SpeedPicker.ItemsSource = Enumerable.Range(1, MouseMotionBinding.MaxSpeed).ToArray();
        SpeedPicker.SelectedItem = selection?.Speed ?? 12;
        LearnButton.IsEnabled = ports.Count > 0;
        BindingStatus.Text = ports.Count > 0 ? "数值越大，旋钮每转一步移动越远。" : "连接 MIDI 设备后即可学习旋钮。";
        ShowSelection(ports.FirstOrDefault(port => port.Key == selection?.Port)?.Name);
    }

    internal void Observe(MidiPort port, MidiSignal signal, Preset preset)
    {
        if (!learning || signal.Id.Kind != SignalKind.Control || signal.Id.Number > 119) return;
        var modeKey = preset.Piano?.ModeKey;
        if (modeKey is not null && modeKey.Kind == signal.Id.Kind && modeKey.Port == port.Key &&
            modeKey.Number == signal.Id.Number && (modeKey.Channel == 0 || modeKey.Channel == signal.Id.Channel))
        {
            BindingStatus.Text = "此控件已用于切换钢琴模式，请转动另一个旋钮。";
            return;
        }
        if (preset.MouseSpeed?.Matches(port.Key, signal.Id) == true)
        {
            BindingStatus.Text = "此控件已绑定鼠标灵敏度，请转动另一个旋钮。";
            return;
        }
        selection = new(port.Key, signal.Id.Channel, signal.Id.Number, (int)SpeedPicker.SelectedItem,
            (MouseMotionMode)ModePicker.SelectedIndex);
        learning = false;
        ShowSelection(port.Name);
        BindingStatus.Text = "已识别旋钮。保存后，方案生效时即可使用。";
    }

    private void ShowSelection(string? port)
    {
        BindingLabel.Text = selection is { } binding
            ? $"{port ?? "设备未连接"} · CH {binding.Channel:D2} · CC {binding.Controller:D3}" : "未绑定";
        BindingLabel.ToolTip = BindingLabel.Text;
        RemoveButton.IsEnabled = selection is not null;
        UpdateLearningControls();
    }

    private void UpdateLearningControls()
    {
        LearnButton.Content = learning ? "停止学习" : "学习旋钮";
        SaveButton.IsEnabled = !learning && selection is not null;
    }

    private void ToggleLearning(object sender, RoutedEventArgs e)
    {
        learning = !learning;
        UpdateLearningControls();
        BindingStatus.Text = learning ? "请转动要绑定的旋钮。" : "已停止学习。";
    }

    private void SaveBinding(object sender, RoutedEventArgs e)
    {
        if (learning || selection is not { } binding || SpeedPicker.SelectedItem is not int speed) return;
        Result = binding with { Speed = speed, Mode = (MouseMotionMode)ModePicker.SelectedIndex };
        DialogResult = true;
    }

    private void RemoveBinding(object sender, RoutedEventArgs e) => DialogResult = true;
}
