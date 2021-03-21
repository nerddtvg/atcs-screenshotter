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

using PInvoke;

namespace atcs_screenshotter
{
    public class Program
    {
        protected static string processName = "atcsmon";

        protected static Dictionary<string, string> windowTitles = new Dictionary<string, string>() {
            { "Terminal Railroad Association", "trra" }
        };

        static void Main(string[] args)
        {
            // Need to get all of our processes
            var processes = Process.GetProcessesByName(processName).Where(a => a.MainWindowHandle.ToString() != "0").ToList();
            
            // Did we get results
            if (processes.Count == 0) {
                Console.WriteLine($"Unable to identify '{processName}' process.");
                return;
            }

            // Get all of the windows
            // https://stackoverflow.com/questions/7803289/c-sharp-how-to-get-all-windows-using-mainwindowhandle
            // http://www.pinvoke.net/default.aspx/user32/EnumChildWindows.html
            // https://stackoverflow.com/questions/19867402/how-can-i-use-enumwindows-to-find-windows-with-a-specific-caption-title
            var ptrs = WindowFilter.FindWindowsWithText(windowTitles.Keys).ToList();

            RECT rct;

            if (!User32.GetClientRect(ptrs[0], out rct)) {
                Console.WriteLine("ERROR: Unable to retrieve the window size.");
                return;
            }

            int width = rct.right - rct.left;
            int height = rct.bottom - rct.top;

            using(Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb)) {
                using(Graphics memoryGraphics = Graphics.FromImage(bmp)) {
                    IntPtr dc = memoryGraphics.GetHdc();

                    if (!User32.PrintWindow(ptrs[0], dc, User32.PrintWindowFlags.PW_CLIENTONLY)) {
                        throw new Exception("Unable to capture the window screenshot.");
                    }

                    memoryGraphics.ReleaseHdc(dc);

                    using(Image img = Image.FromHbitmap(bmp.GetHbitmap())) {
                        img.Save("test.jpg", ImageFormat.Jpeg);
                    }
                }
            }
        }

    }
}
