using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MidiKey;

public partial class App : Application
{
    private static readonly string[] StartupMessages =
    {
        "Preparing MIDI input", "Checking keyboard channels", "Loading preset profiles",
        "Warming up piano samples", "Mapping performance controls", "Tuning audio response",
        "Connecting your instrument", "Building the key layout", "Preparing game profiles",
        "Calibrating velocity layers", "Synchronizing MIDI devices", "Loading sound engine",
        "Resolving control mappings", "Preparing the performance space", "Setting audio levels",
        "Checking input signals", "Preparing keyboard output", "Loading custom presets",
        "Optimizing response time", "Starting GamusicStudio", "Reading controller data",
        "Selecting the right channel", "Balancing piano voices", "Preparing live input",
        "Initializing keyboard map", "Warming the sound bank", "Checking device connection",
        "Restoring your last setup", "Preparing the next note", "Fine tuning key response",
        "Loading saved settings", "Building a better sound", "Unlocking every key",
        "Almost ready to play", "Your music starts here"
    };
    private const int DarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in int value, int size);

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new StartupSplash();
        splash.Show();
        try
        {
            var random = Random.Shared;
            int index = 0;
            int duration = random.Next(3000, 5001);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < duration)
            {
                splash.SetMessage(StartupMessages[index++ % StartupMessages.Length]);
                int delay = random.Next(75, 121);
                await Task.Delay(Math.Min(delay, (int)Math.Max(1, duration - timer.ElapsedMilliseconds)));
            }
        }
        finally
        {
            MainWindow main = new();
            MainWindow = main;
            splash.Close();
            main.Show();
            main.Activate();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }

    internal static void ThemeWindow(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            DwmSetWindowAttribute(handle, DarkMode, 1, sizeof(int));
            SetCaptionColor(window, handle, CaptionColor, "Background");
            SetCaptionColor(window, handle, CaptionTextColor, "White");
            SetCaptionColor(window, handle, BorderColor, "Line");
        };
    }

    private static void SetCaptionColor(Window window, IntPtr handle, int attribute, string resource)
    {
        var color = ((SolidColorBrush)window.FindResource(resource)).Color;
        int rgb = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, attribute, rgb, sizeof(int));
    }
}

internal sealed class StartupSplash : Window
{
    private readonly TextBlock loading = new();

    internal StartupSplash()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Width = SystemParameters.PrimaryScreenHeight * 5 / 12;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        using var imageStream = typeof(StartupSplash).Assembly.GetManifestResourceStream("StartupImage.png")
            ?? throw new InvalidOperationException("启动图片加载失败");
        var picture = new BitmapImage();
        picture.BeginInit();
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.StreamSource = imageStream;
        picture.EndInit();
        Height = Width * picture.PixelHeight / picture.PixelWidth;
        double scale = Width / picture.PixelWidth;
        var scene = new Grid();
        scene.Children.Add(new Image { Source = picture, Stretch = Stretch.UniformToFill });
        var backdrop = new LinearGradientBrush();
        backdrop.StartPoint = new Point(0, 0);
        backdrop.EndPoint = new Point(0, 1);
        backdrop.GradientStops.Add(new GradientStop(Color.FromArgb(108, 0, 0, 0), 0));
        backdrop.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
        backdrop.GradientStops.Add(new GradientStop(Color.FromArgb(145, 0, 0, 0), 1));
        scene.Children.Add(new Border { Background = backdrop });
        loading.FontFamily = new FontFamily("Consolas");
        loading.FontSize = Math.Max(7, 28 * scale);
        loading.Foreground = Brushes.White;
        loading.VerticalAlignment = VerticalAlignment.Bottom;
        loading.Margin = new Thickness(50 * scale, 0, 30 * scale, 83 * scale);
        scene.Children.Add(loading);
        var version = new TextBlock
        {
            Text = "Beta V1.2", Foreground = new SolidColorBrush(Color.FromRgb(202, 202, 202)),
            FontFamily = new FontFamily("Consolas"), FontSize = Math.Max(7, 24 * scale),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 25 * scale, 18 * scale)
        };
        scene.Children.Add(version);
        Content = scene;
    }

    internal void SetMessage(string message) => loading.Text = message;
}
