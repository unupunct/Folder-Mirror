using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FolderMirror.Services;

public static class ThemeManager
{
    public static readonly string[] Accents = { "Green", "Blue", "Teal", "Orange", "Purple" };

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static (string dark, string light) Accent(string name) => name switch
    {
        "Blue" => ("#4C9AFF", "#1F6FEB"),
        "Teal" => ("#2DD4BF", "#0F9D8F"),
        "Orange" => ("#FFA43A", "#D9730D"),
        "Purple" => ("#B388FF", "#7E57C2"),
        _ => ("#3DDC84", "#1E9E57")
    };

    public static bool IsDark { get; private set; } = true;

    public static void Apply(bool dark, string accent)
    {
        IsDark = dark;
        var res = Application.Current.Resources;
        void B(string key, string hex) => res[key] = Freeze(new SolidColorBrush(C(hex)));
        var acc = Accent(accent);
        var a = dark ? acc.dark : acc.light;

        if (dark)
        {
            B("BgBrush", "#17191C"); B("SidebarBrush", "#121417"); B("SurfaceBrush", "#1F2226"); B("Surface2Brush", "#282C31");
            B("HoverBrush", "#31363C"); B("BorderBrush", "#343A41"); B("TextBrush", "#E8EAED"); B("TextDimBrush", "#98A0A9");
            B("AccentFgBrush", "#08170E"); B("ShadowTextBrush", "#000000");
            B("NewBrush", "#3DDC84"); B("ModifiedBrush", "#4C9AFF"); B("DeleteBrush", "#FF6B6B"); B("UnchangedBrush", "#8A9099"); B("ConflictBrush", "#FFA43A");
            B("WarnBgBrush", "#3A2F1B"); B("ErrorBgBrush", "#3C2325"); B("InfoBgBrush", "#1E2C3A");
        }
        else
        {
            B("BgBrush", "#F1F3F6"); B("SidebarBrush", "#E4E8EC"); B("SurfaceBrush", "#FFFFFF"); B("Surface2Brush", "#EDF0F3");
            B("HoverBrush", "#DDE2E7"); B("BorderBrush", "#D3D9DF"); B("TextBrush", "#1B1F23"); B("TextDimBrush", "#5B6570");
            B("AccentFgBrush", "#FFFFFF"); B("ShadowTextBrush", "#000000");
            B("NewBrush", "#1E9E57"); B("ModifiedBrush", "#1F6FEB"); B("DeleteBrush", "#D1383D"); B("UnchangedBrush", "#7A828B"); B("ConflictBrush", "#D9730D");
            B("WarnBgBrush", "#FFF1D6"); B("ErrorBgBrush", "#FDE3E4"); B("InfoBgBrush", "#E1EEFB");
        }
        B("AccentBrush", a);
        var ac = C(a);
        res["AccentHoverBrush"] = Freeze(new SolidColorBrush(Color.FromRgb((byte)Math.Min(255, ac.R + 24), (byte)Math.Min(255, ac.G + 24), (byte)Math.Min(255, ac.B + 24))));
        res["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(46, ac.R, ac.G, ac.B)));
        res["AccentColor"] = ac;

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int v = IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref v, sizeof(int));
            DwmSetWindowAttribute(h, 19, ref v, sizeof(int));
        }
        catch { }
    }

    public static void Hook(Window w)
    {
        w.SourceInitialized += (_, _) => ApplyTitleBar(w);
    }
}
