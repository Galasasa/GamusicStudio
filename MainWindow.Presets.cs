using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;

namespace MidiKey;

public partial class MainWindow
{
    private sealed record PresetItem(Preset Preset, bool IsActive)
    {
        public string Name => Preset.Name;
        public string Details => $"{Preset.Rules.Length} 条换键";
        public ImageSource? Logo { get; } = LoadPresetLogo(Preset.Artwork);
        public Brush Surface { get; } = PresetSurface(Preset);
    }

    private Guid? editorPresetId;
    private Guid? selectedPresetId;
    private string? unflashedPort;
    private bool updatingPresetList;
    private CancellationTokenSource? flashCancellation;
    private static readonly Preset Unflashed = new(Guid.Empty, "", null, Array.Empty<Rule>());
    private Preset CurrentPreset => editorPresetId is Guid id ? settings.Find(id) : settings.Active ?? Unflashed;
    private bool PreviewingInactivePreset => editorPresetId is Guid id && id != settings.ActivePresetId;
    private bool CanSendMappedKeys => settings.ActivePresetId.HasValue && !PreviewingInactivePreset && !fullKeyboardPiano;

    private void UpdatePresetList()
    {
        string query = PresetSearch.Text.Trim();
        var results = settings.Presets
            .Where(preset => preset.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                GamePresets.FindArtwork(preset.Artwork)?.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(preset => new PresetItem(preset, preset.Id == settings.ActivePresetId))
            .ToArray();
        var selection = results.FirstOrDefault(item => item.Preset.Id == selectedPresetId);
        updatingPresetList = true;
        PresetList.ItemsSource = results;
        PresetList.SelectedItem = selection;
        updatingPresetList = false;
        selectedPresetId = selection?.Preset.Id;
        PresetResultCount.Text = $"{results.Length} 个夺舍程序";
        PresetEmpty.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFlashControls();
    }

    private static ImageSource? LoadPresetLogo(string? artwork)
    {
        var game = GamePresets.FindArtwork(artwork);
        return game is null ? null : BitmapFrame.Create(
            new Uri($"pack://application:,,,/Assets/Presets/{game.Artwork}.png", UriKind.Absolute));
    }

    private static Brush PresetSurface(Preset preset)
    {
        if (GamePresets.FindArtwork(preset.Artwork) is null) return PresetPattern.Create(preset.Id);
        Color color = preset.Artwork switch
        {
            "valorant" => Color.FromRgb(79, 21, 45),
            "tarkov" => Color.FromRgb(51, 56, 43),
            "delta" => Color.FromRgb(21, 61, 56),
            "titanfall" => Color.FromRgb(22, 52, 78),
            _ => Color.FromRgb(48, 22, 67)
        };
        var gradient = new LinearGradientBrush(color, Color.FromRgb(12, 9, 23), 62);
        gradient.Freeze();
        return gradient;
    }

    private void PresetSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!ready) return;
        UpdatePresetList();
        PresetScroll.ScrollToHorizontalOffset(0);
    }

    private void PresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updatingPresetList) return;
        selectedPresetId = (PresetList.SelectedItem as PresetItem)?.Preset.Id;
        PresetStatus.Text = "";
        UpdateFlashControls();
    }

    private void UpdateFlashControls()
    {
        bool flashing = flashCancellation is not null;
        var selected = PresetList.SelectedItem as PresetItem;
        FlashButton.Visibility = flashing ? Visibility.Collapsed : Visibility.Visible;
        FlashMonitor.Visibility = flashing ? Visibility.Visible : Visibility.Collapsed;
        FlashButton.IsEnabled = selected is not null && !flashing;
        CancelPresetButton.Visibility = settings.ActivePresetId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        CancelPresetButton.IsEnabled = !flashing;
        PresetCopyButton.IsEnabled = settings.ActivePresetId.HasValue;
        PresetList.IsEnabled = PresetSearch.IsEnabled = PresetCreateActions.IsEnabled = DeviceToolbar.IsEnabled = !flashing;
    }

    private void ClearPresetSearch(object sender, RoutedEventArgs e)
    {
        PresetSearch.Clear();
        PresetSearch.Focus();
    }

    private void ScrollPresetCards(object sender, MouseWheelEventArgs e)
    {
        if (PresetScroll.ScrollableWidth <= 0) return;
        double offset = PresetScroll.HorizontalOffset - (258d / Mouse.MouseWheelDeltaForOneLine) * e.Delta;
        PresetScroll.ScrollToHorizontalOffset(Math.Clamp(offset, 0, PresetScroll.ScrollableWidth));
        e.Handled = true;
    }

    private void ResizePresetViewport(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0) return;
        PresetViewport.Resources["PresetArtHeight"] = Math.Clamp(e.NewSize.Height - 75, 125, 208);
    }

    private void UpdateOutputControls()
    {
        OutputButton.IsEnabled = CanSendMappedKeys;
        OutputButton.Content = fullKeyboardPiano ? "♪  全键盘钢琴" : PreviewingInactivePreset ? "○  编辑中 · 输出暂停" :
            !CanSendMappedKeys ? "○  未刷写程序" : output ? "●  按键输出已开启" : "○  按键输出已暂停";
        OutputButton.Foreground = Brush(fullKeyboardPiano || (output && CanSendMappedKeys) ? "Accent" : "Muted");
    }

    private void OpenPreset(object sender, RoutedEventArgs e)
    {
        if (flashCancellation is not null || sender is not FrameworkElement { DataContext: PresetItem item }) return;
        bool reload = item.Preset.Id != CurrentPreset.Id;
        editorPresetId = item.Preset.Id;
        PresetScreen.Visibility = Visibility.Collapsed;
        PresetWorkspace.Visibility = WorkspaceActions.Visibility = Visibility.Visible;
        AnimateScreen(PresetWorkspace);
        LibraryBrand.Visibility = Visibility.Collapsed;
        PresetEditorName.Visibility = Visibility.Visible;
        FitWorkspaceWindow();
        ShowEditorName();
        UpdateOutputControls();
        if (reload) Discover(true);
        OutputStatus.Text = PreviewingInactivePreset ? "此方案尚未生效。编辑期间暂停按键输出。" :
            "修改将保存到此方案。";
    }

    private void ReturnToPresets(object sender, RoutedEventArgs e)
    {
        StopLearningPianoModeKey();
        bool reload = PreviewingInactivePreset;
        editorPresetId = null;
        PresetWorkspace.Visibility = WorkspaceActions.Visibility = Visibility.Collapsed;
        PresetScreen.Visibility = Visibility.Visible;
        AnimateScreen(PresetScreen);
        LibraryBrand.Visibility = Visibility.Visible;
        PresetEditorName.Visibility = Visibility.Collapsed;
        MinHeight = 640;
        UpdateOutputControls();
        if (reload) Discover(true);
        UpdatePresetList();
        PresetStatus.Text = "";
    }

    private static void AnimateScreen(UIElement screen)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        screen.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void CancelActivePreset(object sender, RoutedEventArgs e)
    {
        if (flashCancellation is not null || settings.ActivePresetId is null ||
            !SaveLibrary(settings.Presets, null)) return;
        unflashedPort = primary.Port?.Key;
        keys.Cancel();
        pianoSound.Dispose();
        if (editorPresetId is not null) ReturnToPresets(sender, e);
        else
        {
            Discover(true);
            UpdateOutputControls();
            UpdatePresetList();
        }
        PresetStatus.Text = "已取消夺舍";
    }

    private async void FlashPreset(object sender, RoutedEventArgs e)
    {
        if (flashCancellation is not null || PresetList.SelectedItem is not PresetItem item) return;
        using var cancellation = new CancellationTokenSource();
        flashCancellation = cancellation;
        FlashProgramName.Text = item.Name;
        var sequence = new FlashSequence(Random.Shared);
        RenderFlash(sequence.Sample(TimeSpan.Zero));
        UpdateFlashControls();
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (elapsed.Elapsed < sequence.Duration)
            {
                RenderFlash(sequence.Sample(elapsed.Elapsed));
                await Task.Delay(32, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!SaveLibrary(settings.Presets, item.Preset.Id)) return;
            Discover(true);
            UpdateOutputControls();
            UpdatePresetList();
            FlashProgress.Value = 100;
            FlashPercentage.Text = "100%";
            FlashCommand.Text = "刷写完成：Program active";
            PresetStatus.Text = $"刷写完成 · 「{item.Name}」已生效";
            await Task.Delay(170, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            flashCancellation = null;
            if (!closing) UpdateFlashControls();
        }
    }

    private void RenderFlash((double Progress, string Command) sample)
    {
        FlashProgress.Value = sample.Progress;
        FlashPercentage.Text = $"{(int)sample.Progress}%";
        FlashCommand.Text = $"正在刷写：{sample.Command}";
    }

    private void CreatePreset(object sender, RoutedEventArgs e) => AddPreset(copyCurrent: false);
    private void SavePresetCopy(object sender, RoutedEventArgs e) => AddPreset(copyCurrent: true);

    private void AddPreset(bool copyCurrent)
    {
        if (flashCancellation is not null) return;
        var current = CurrentPreset;
        if (copyCurrent && current.Id == Guid.Empty) return;
        string heading = copyCurrent ? "另存夺舍程序" : "新建夺舍程序";
        string initialName = copyCurrent ? SuggestCopyName(current.Name) : "";
        if (RequestPresetName(heading, initialName) is not string name) return;

        var preset = new Preset(Guid.NewGuid(), name, copyCurrent ? current.Port : null,
            copyCurrent ? current.Rules.ToArray() : Array.Empty<Rule>(), Piano: copyCurrent ? current.Piano : null);
        if (!SaveLibrary(settings.Presets.Prepend(preset).ToArray(), settings.ActivePresetId)) return;
        PresetSearch.Clear();
        PresetScroll.ScrollToHorizontalOffset(0);
        selectedPresetId = preset.Id;
        UpdatePresetList();
        SetPresetStatus($"已保存夺舍程序「{name}」。");
    }

    private string SuggestCopyName(string original)
    {
        const string suffix = " 副本";
        string baseName = original[..Math.Min(original.Length, SettingsStore.MaxNameLength - suffix.Length - 6)];
        string candidate = baseName + suffix;
        for (int index = 2; settings.Presets.Any(preset => preset.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); index++)
            candidate = $"{baseName}{suffix} {index}";
        return candidate;
    }

    private void RenamePreset(object sender, RoutedEventArgs e)
    {
        var current = CurrentPreset;
        if (RequestPresetName("重命名夺舍程序", current.Name, current.Id) is not string name ||
            name == current.Name || !SavePreset(current with { Name = name })) return;
        ShowEditorName();
        UpdatePresetList();
        SetPresetStatus($"夺舍程序已重命名为「{name}」。");
    }

    private void ShowEditorName()
    {
        PresetEditorName.Text = $"方案编写（{CurrentPreset.Name}）";
        PresetEditorName.ToolTip = PresetEditorName.Text;
    }

    private string? RequestPresetName(string heading, string name, Guid? excludeId = null)
    {
        editing = true;
        keys.Cancel();
        try
        {
            var names = settings.Presets.Where(preset => preset.Id != excludeId).Select(preset => preset.Name);
            var dialog = new PresetNameWindow(heading, name, names) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.PresetName : null;
        }
        finally { editing = false; }
    }

    private void SetPresetStatus(string text)
    {
        if (editorPresetId is null) PresetStatus.Text = text;
        else OutputStatus.Text = text;
    }

    private bool SavePreset(Preset changed) => SaveLibrary(
        settings.Presets.Select(preset => preset.Id == changed.Id ? changed : preset).ToArray(),
        settings.ActivePresetId);

    private bool SaveLibrary(Preset[] presets, Guid? activeId)
    {
        var next = new SettingsStore { Presets = presets, ActivePresetId = activeId };
        try
        {
            next.Write();
            settings = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetPresetStatus("保存配置失败 · " + ex.Message);
            return false;
        }
    }
}
