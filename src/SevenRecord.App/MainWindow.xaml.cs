using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.UI;

namespace SevenRecord.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        ApplyTitleBarColors();

        RootFrame.Navigate(typeof(MainPage));
    }

    private void ApplyTitleBarColors()
    {
        bool highContrast = IsHighContrastEnabled();
        Color titleBackground = highContrast
            ? GetSystemColor(ColorWindow)
            : Color.FromArgb(255, 24, 22, 24);
        Color titleForeground = highContrast
            ? GetSystemColor(ColorWindowText)
            : Color.FromArgb(255, 248, 244, 246);
        Color hoverBackground = highContrast
            ? GetSystemColor(ColorHighlight)
            : Color.FromArgb(255, 36, 33, 36);
        Color pressedBackground = highContrast
            ? GetSystemColor(ColorHighlight)
            : Color.FromArgb(255, 48, 44, 48);

        AppWindow.TitleBar.BackgroundColor = titleBackground;
        AppWindow.TitleBar.InactiveBackgroundColor = titleBackground;
        AppWindow.TitleBar.ButtonBackgroundColor = titleBackground;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = titleBackground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = hoverBackground;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = pressedBackground;
        AppWindow.TitleBar.ButtonForegroundColor = titleForeground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor =
            highContrast
                ? titleForeground
                : Color.FromArgb(255, 178, 170, 176);
    }

    private static bool IsHighContrastEnabled()
    {
        HighContrast settings = new()
        {
            Size = (uint)Marshal.SizeOf<HighContrast>(),
        };
        return SystemParametersInfo(
                SpiGetHighContrast,
                settings.Size,
                ref settings,
                0) &&
            (settings.Flags & HighContrastOn) != 0;
    }

    private static Color GetSystemColor(int index)
    {
        uint color = GetSysColor(index);
        return Color.FromArgb(
            255,
            (byte)(color & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)((color >> 16) & 0xFF));
    }

    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;
    private const int ColorWindow = 5;
    private const int ColorWindowText = 8;
    private const int ColorHighlight = 13;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrast data,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
