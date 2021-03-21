using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// DLL Import
using System.Runtime.InteropServices;

// StringBuilder
using System.Text;

// Capturing the window
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

// Processes
using System.Diagnostics;

namespace atcs_screenshotter
{
    public class WindowFilter
    {
        // https://stackoverflow.com/questions/19867402/how-can-i-use-enumwindows-to-find-windows-with-a-specific-caption-title
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        // Delegate to filter which windows to include 
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary> Get the text for the window pointed to by hWnd </summary>
        public static string GetWindowText(IntPtr hWnd)
        {
            int size = GetWindowTextLength(hWnd);
            if (size > 0)
            {
                var builder = new StringBuilder(size + 1);
                GetWindowText(hWnd, builder, builder.Capacity);
                return builder.ToString();
            }

            return String.Empty;
        }

        /// <summary> Find all windows that match the given filter </summary>
        /// <param name="filter"> A delegate that returns true for windows
        ///    that should be returned and false for windows that should
        ///    not be returned </param>
        public static IEnumerable<IntPtr> FindWindows(EnumWindowsProc filter)
        {
            IntPtr found = IntPtr.Zero;
            List<IntPtr> windows = new List<IntPtr>();

            EnumWindows(delegate(IntPtr wnd, IntPtr param)
            {
                if (filter(wnd, param))
                {
                    // only add the windows that pass the filter
                    windows.Add(wnd);
                }

                // but return true here so that we iterate all windows
                return true;
            }, IntPtr.Zero);

            return windows;
        }

        /// <summary> Find all windows that contain the given title text </summary>
        /// <param name="titleText"> The text that the window title must contain. </param>
        public static IEnumerable<IntPtr> FindWindowsWithText(string titleText, bool exactMatch = false)
        {
            return FindWindowsWithText(new List<string>() { titleText }, exactMatch);
        }

        /// <summary> Find all windows that contain the given title text </summary>
        /// <param name="titleText"> The text that the window title must contain or match. </param>
        /// <param name="exactMatch"> Should this be an exact match or only contain the desired string. </param>
        public static IEnumerable<IntPtr> FindWindowsWithText(ICollection<string> titleText, bool exactMatch = false)
        {
            return FindWindows(delegate(IntPtr wnd, IntPtr param)
            {
                var windowTitle = GetWindowText(wnd);

                // No blank windows
                if (string.IsNullOrEmpty(windowTitle)) return false;

                return (exactMatch ? titleText.Any(a => a.Equals(windowTitle, StringComparison.InvariantCultureIgnoreCase)) : titleText.Any(a => a.Contains(windowTitle, StringComparison.InvariantCultureIgnoreCase)));
            });
        }

    }
}
