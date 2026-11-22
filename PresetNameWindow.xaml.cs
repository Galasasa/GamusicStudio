using System.Windows;

namespace MidiKey;

public partial class PresetNameWindow : Window
{
    private readonly HashSet<string> existingNames;
    internal string PresetName { get; private set; } = "";

    internal PresetNameWindow(string heading, string name, IEnumerable<string> existingNames)
    {
        this.existingNames = new(existingNames, StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        App.ThemeWindow(this);
        Title = Heading.Text = heading;
        NameBox.MaxLength = SettingsStore.MaxNameLength;
        NameBox.Text = name;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void SaveName(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            NameError.Text = "请输入夺舍程序名称。";
            NameBox.Focus();
            return;
        }
        if (existingNames.Contains(name))
        {
            NameError.Text = "已有同名夺舍程序，请换一个名称。";
            NameBox.Focus();
            NameBox.SelectAll();
            return;
        }
        PresetName = name;
        DialogResult = true;
    }
}
