using System;
using System.Runtime.InteropServices;

namespace QuickNote;

internal static partial class NativeMethods
{
    internal const string WindowTitle = "QuickNote · 晾一会";
    internal static readonly uint ShowMessage = RegisterWindowMessage("QuickNote.Clothesline.Show.v1");
    internal const int WmHotkey = 0x0312;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static void NotifyExistingInstance()
    {
        PostMessage(FindWindow(null, WindowTitle), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }
}
