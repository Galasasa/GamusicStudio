using System.Windows;

namespace MidiKey;

public partial class MainWindow
{
    private MouseSpeedWindow? mouseSpeedEditor;

    private void EditMouseSpeed(object sender, RoutedEventArgs e)
    {
        var preset = CurrentPreset;
        StopLearningPianoModeKey();
        var connected = new[] { primary.Port, daw.Port }.OfType<MidiPort>().ToArray();
        var dialog = new MouseSpeedWindow(preset.MouseSpeed, connected, ApplyMouseSpeed) { Owner = this };
        editing = true;
        keys.Cancel();
        mouseSpeedEditor = dialog;
        try
        {
            if (dialog.ShowDialog() == true && SavePreset(preset with { MouseSpeed = dialog.Result }))
            {
                UpdateMouseSpeedControl();
                OutputStatus.Text = dialog.Result is null ? "已清除鼠标灵敏度绑定。" : "鼠标灵敏度绑定已保存到此方案。";
            }
        }
        finally
        {
            mouseSpeedEditor = null;
            editing = false;
        }
    }

    private void UpdateMouseSpeedControl()
    {
        var binding = CurrentPreset.MouseSpeed;
        MouseSpeedButton.ToolTip = binding is null ? "指定 MIDI 推子或旋钮来调整 Windows 鼠标灵敏度。" :
            $"CH {binding.Channel:D2} · {binding.Label} · 灵敏度 {binding.Minimum}–{binding.Maximum}";
    }

    private void ApplyMouseSpeed(int speed)
    {
        var current = WindowsMouseSpeed.Read();
        if (current != speed && !WindowsMouseSpeed.Apply(speed))
        {
            OutputStatus.Text = "无法调整 Windows 鼠标灵敏度。";
            mouseSpeedEditor?.ShowSpeedWriteError(current);
            return;
        }
        if (current != speed) OutputStatus.Text = $"Windows 鼠标灵敏度 · {speed} / 20";
        mouseSpeedEditor?.ShowCurrentSpeed(speed);
    }
}
