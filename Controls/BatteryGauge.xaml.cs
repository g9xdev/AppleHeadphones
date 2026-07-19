using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AirPodsBattery.Controls;

/// <summary>
/// A circular battery gauge: determinate progress ring, centred percentage,
/// charging bolt, and an optional label underneath. Colour-coded by level and
/// dimmed when the reading is stale.
/// </summary>
public sealed partial class BatteryGauge : UserControl
{
    private static readonly Color Green = Color.FromArgb(255, 74, 222, 128);
    private static readonly Color Amber = Color.FromArgb(255, 251, 191, 36);
    private static readonly Color Red = Color.FromArgb(255, 248, 113, 113);
    private static readonly Color Gray = Color.FromArgb(255, 156, 163, 175);

    public BatteryGauge()
    {
        InitializeComponent();
    }

    public void SetState(string label, int? level, bool charging, bool stale)
    {
        LabelText.Text = label;
        LabelText.Visibility = string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;

        if (level is int p)
        {
            Ring.Value = p;
            PercentText.Text = $"{p}%";
            Ring.Foreground = new SolidColorBrush(
                stale ? Gray : p >= 50 ? Green : p >= 20 ? Amber : Red);
        }
        else
        {
            Ring.Value = 0;
            PercentText.Text = "—";
            Ring.Foreground = new SolidColorBrush(Gray);
        }

        BoltText.Visibility = charging && !stale ? Visibility.Visible : Visibility.Collapsed;
        Opacity = stale ? 0.45 : 1.0;
    }
}
