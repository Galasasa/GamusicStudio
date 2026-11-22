using System.Windows;

namespace MidiKey;

public partial class MainWindow
{
    private MouseMotionWindow? mouseMotionEditor;

    private void EditMouseMotion(object sender, RoutedEventArgs e)
    {
        var preset = CurrentPreset;
        StopLearningPianoModeKey();
        var connected = new[] { primary.Port, daw.Port }.OfType<MidiPort>().ToArray();
        var dialog = new MouseMotionWindow(preset, connected) { Owner = this };
        editing = true;
        keys.Cancel();
        mouseMotionEditor = dialog;
        try
        {
            if (dialog.ShowDialog() != true || !SavePreset(preset with { MouseMotion = dialog.Result })) return;
            UpdateMouseMotionControl();
            OutputStatus.Text = dialog.Result is null ? "已清除鼠标横移绑定。" : "鼠标横移绑定已保存到此方案。";
        }
        finally
        {
            mouseMotionEditor = null;
            editing = false;
        }
    }

    private void UpdateMouseMotionControl()
    {
        var binding = CurrentPreset.MouseMotion;
        MouseMotionButton.ToolTip = binding is null ? "指定 MIDI 旋钮左右移动鼠标。" :
            $"CH {binding.Channel:D2} · CC {binding.Controller:D3} · 移动速度 {binding.Speed}";
    }
}
