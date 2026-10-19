using System.Runtime.InteropServices;

namespace SignFonts;

internal sealed class WindowsFontCatalog : IDisposable
{
    private const uint PrivateFont = 0x10;
    private readonly HashSet<string> registeredFiles = new(StringComparer.OrdinalIgnoreCase);

    internal string[] RegisterDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory)
            .Where(IsFontFile)
            .Where(path => !RegisterFontFile(path))
            .ToArray();
    }

    internal bool RegisterFontFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !IsFontFile(path)) return false;
        var fullPath = Path.GetFullPath(path);
        if (registeredFiles.Contains(fullPath)) return true;
        // Process-private fonts are visible to this game and its font enumeration.
        if (AddFontResourceExW(fullPath, PrivateFont, IntPtr.Zero) <= 0) return false;
        registeredFiles.Add(fullPath);
        return true;
    }

    internal static string[] GetInstalledFamilies()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var deviceContext = GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero) throw new InvalidOperationException("Windows could not create a font enumeration context.");
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filter = new LogFont { CharSet = 1, FaceName = "" }; // DEFAULT_CHARSET, all families.
            FontCallback callback = (fontPointer, _, _, _) =>
            {
                var family = Marshal.PtrToStructure<LogFont>(fontPointer).FaceName?.Trim();
                // Windows also exposes vertical aliases prefixed with @.
                if (!string.IsNullOrWhiteSpace(family) && !family.StartsWith('@')) names.Add(family);
                return 1;
            };
            EnumFontFamiliesExW(deviceContext, ref filter, callback, IntPtr.Zero, 0);
            GC.KeepAlive(callback);
            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, deviceContext);
        }
    }

    private static bool IsFontFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc";

    public void Dispose()
    {
        foreach (var path in registeredFiles) RemoveFontResourceExW(path, PrivateFont, IntPtr.Zero);
        registeredFiles.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FontCallback(IntPtr font, IntPtr metrics, uint fontType, IntPtr parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int EnumFontFamiliesExW(IntPtr deviceContext, ref LogFont filter, FontCallback callback, IntPtr parameter, uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddFontResourceExW(string path, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool RemoveFontResourceExW(string path, uint flags, IntPtr reserved);
}
