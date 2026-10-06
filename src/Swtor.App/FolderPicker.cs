using System.Runtime.InteropServices;

namespace Swtor.App;

/// <summary>
/// Native Windows folder dialog (shell API, no WinForms). It blocks, so call <see cref="PickAsync"/>.
/// </summary>
internal static class FolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x1;
    private const uint BifNewDialogStyle = 0x40;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr Owner;
        public IntPtr Root;
        public IntPtr DisplayName;
        public string Title;
        public uint Flags;
        public IntPtr Callback;
        public IntPtr Param;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr idList, [Out] char[] path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    /// <summary>Shows the dialog on its own STA thread. Returns the chosen folder, or null if the user cancels.</summary>
    public static Task<string?> PickAsync(string title)
    {
        var done = new TaskCompletionSource<string?>();
        if (!OperatingSystem.IsWindows())
        {
            done.SetResult(null);
            return done.Task;
        }
        // The shell dialog needs a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try { done.SetResult(Show(title)); }
            catch (Exception e) { done.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    private static string? Show(string title)
    {
        var info = new BrowseInfo { Title = title, Flags = BifReturnOnlyFsDirs | BifNewDialogStyle };
        IntPtr idList = SHBrowseForFolder(ref info);
        if (idList == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            return SHGetPathFromIDList(idList, buffer) ? new string(buffer).TrimEnd('\0') : null;
        }
        finally { CoTaskMemFree(idList); }
    }
}
