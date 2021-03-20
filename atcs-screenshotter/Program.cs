using System;

using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace atcs_screenshotter
{
    class Program
    {
        static void Main(string[] args)
        {
            // https://stackoverflow.com/questions/10741384/how-can-i-get-a-screenshot-of-control-drawtobitmap-not-working
            Bitmap BMP = new Bitmap(Screen.PrimaryScreen.Bounds.Width,
                Screen.PrimaryScreen.Bounds.Height,
                PixelFormat.Format32bppArgb);

            using (Graphics GFX = Graphics.FromImage(BMP))
            {
                GFX.CopyFromScreen(Screen.PrimaryScreen.Bounds.X,
                    Screen.PrimaryScreen.Bounds.Y,
                    0, 0,
                    Screen.PrimaryScreen.Bounds.Size,
                    CopyPixelOperation.SourceCopy);
            }

            Image img = Image.FromHbitmap(BMP.GetHbitmap());
            img.Save("test.jpg", ImageFormat.Jpeg);
        }
    }
}
