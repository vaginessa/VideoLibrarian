﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MovieGuide
{
    public enum Severity
    {
        None,    //No severity level written
        Success, //Action successful, Color.Green
        Error,   //Action failed, Color.Red
        Warning, //Action failed but recovered, Color.Gold
        Info,    //Action status, Color.Blue
        Verbose  //Detailed action status, Color.Purple
    }

    public static class Log
    {
        private static readonly string _logName = Path.ChangeExtension(Process.GetCurrentProcess().MainModule.FileName, ".log");
        public static string LogName { get { return _logName; } }
        private static StreamWriter LogStream = null;
        private static readonly object lockObj = new object();

        public static event Action<Severity, string> MessageCapture;

        public static void Write(Severity severity, string fmt, params object[] args)
        {
            if (fmt == null && LogStream == null) return; //Nothing to do
            if (fmt == null && LogStream != null) //Close
            {
                lock (lockObj) { LogStream.Close(); LogStream.Dispose(); LogStream = null; }
                return;
            }
            if (fmt != null && LogStream == null) //Open
            {
                lock (lockObj)
                {
                    //Roll over log at 100MB
                    if (File.Exists(LogName) && new FileInfo(LogName).Length > (1024 * 1024 * 100)) File.Delete(LogName);
                    var fs = File.Open(LogName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    LogStream = new StreamWriter(fs) { AutoFlush = true };
                    LogStream.WriteLine(@"-------- {0:MM/dd/yyyy hh:mm:ss tt} ------------------------------------------", DateTime.Now); 
                }
            }

            if (LogStream != null) lock (lockObj)
            {
                if (severity != Severity.None) LogStream.Write(severity.ToString() + ": ");
#if DEBUG
                    //Cleanup string and indent succeeding lines
                    if (args != null && args.Length > 0)
                    fmt = string.Format(fmt, args);
                fmt = fmt.Beautify(false, "    ").TrimStart();
                LogStream.WriteLine(fmt);
#else
                //Cleanup string and indent succeeding lines. But as this is release
                //mode, exceptions show only the message not the entire call stack.
                //Users wouldn't know what to do with the call stack, anyway.
                if (args != null && args.Length > 0)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] is Exception) args[i] = ((Exception)args[i]).Message;
                    }
                    fmt = string.Format(fmt, args);
                }
                fmt = fmt.Beautify(false, "    ").TrimStart();
                LogStream.WriteLine(fmt); 
#endif
                LogStream.BaseStream.Flush();
                MessageCapture?.Invoke(severity, fmt);
            }
        }
    }

    public static class Diagnostics
    {
        /// <summary>
        /// Write string to debug output.
        /// Uses Win32 OutputDebugString() or System.Diagnostics.Trace.Write() if running under a debugger.
        /// The reason for all this trickery is due to the fact that OutputDebugString() output DOES NOT get
        /// written to VisualStudio output window. Trace.Write() does write to the VisualStudio output window
        /// (by virtue of OutputDebugString somewhere deep inside), BUT it also is can be redirected
        /// to other destination(s) in the app config. This API Delegate is a compromise.
        /// </summary>
        private static readonly WriteDelegate _rawWrite = (System.Diagnostics.Debugger.IsAttached ? (WriteDelegate)new System.Diagnostics.DefaultTraceListener().Write : (WriteDelegate)OutputDebugString);
        private delegate void WriteDelegate(string msg);
        [DllImport("Kernel32.dll")]  private static extern void OutputDebugString(string errmsg);

        [Conditional("DEBUG")]
        public static void WriteLine(string msg, params object[] args)
        {
            if (args != null && args.Length > 0) msg = string.Format(msg, args);
            if (msg[msg.Length - 1] != '\n') msg += Environment.NewLine;
            //Prefix diagnostic message with something unique that can be filtered upon by DebugView.exe
            _rawWrite("DEBUG: " + msg);
        }
    }

    public class EqualityComparer<T> : IEqualityComparer, IEqualityComparer<T>
    {
        private Func<T, T, bool> _equals;
        private Func<T, int> _hashCode;

        public EqualityComparer()
        {
        }

        public EqualityComparer(Func<T, T, bool> equals)
        {
            _equals = equals;
        }

        public EqualityComparer(Func<T, T, bool> equals, Func<T, int> hashCode)
        {
            _equals = equals;
            _hashCode = hashCode;
        }

        public bool Equals(T x, T y)
        {
            if (x==null && y==null) return true;
            if (x==null || y==null) return false;
            return _equals==null ? x.Equals(y) : _equals(x,y);
        }

        public int GetHashCode(T obj)
        {
            return _hashCode==null ? obj.GetHashCode() : _hashCode(obj);
        }

        public new bool Equals(object x, object y)
        {
            return Equals((T)x, (T)y);
        }

        public int GetHashCode(object obj)
        {
            return GetHashCode((T)obj);
        }
    }

    public static class DateTimeEx
    {
        public static readonly DateTime Epoch = new DateTime(1970, 1, 1);
        private static readonly string dateFormat = GetDateFormat();

        /// <summary>
        /// Get localized date WITHOUT the day-of-week. Can't use "D" format because it includes the day-of-week!
        /// </summary>
        /// <param name="dt"></param>
        /// <returns></returns>
        public static string ToDateString(this DateTime dt)
        {
            return dt.ToString(dateFormat);
        }

        //Get localized date format WITHOUT the day-of-week. Can't use "D" format because it includes the day-of-week!
        private static string GetDateFormat()
        {
            var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            string[] patterns = dtf.GetAllDateTimePatterns();
            string longPattern = dtf.LongDatePattern;
            string acceptablePattern = String.Empty;

            foreach (string pattern in patterns)
            {
                if (longPattern.Contains(pattern) && !pattern.Contains("ddd") && !pattern.Contains("dddd"))
                {
                    if (pattern.Length > acceptablePattern.Length)
                    {
                        acceptablePattern = pattern;
                    }
                }
            }

            if (String.IsNullOrEmpty(acceptablePattern))
            {
                return longPattern;
            }
            return acceptablePattern;
        }
    }

    public static class CommonExtensions
    {
        public static string Beautify(this string s, bool stripComments, string indent)
        {
            if (stripComments)
            {
                s = Regex.Replace(s, @"^[ \t]*(--|//).*?\r\n", "", RegexOptions.Multiline); //remove whole line sql or c++ comments
                s = Regex.Replace(s, @"[ \t]*(--|//).*?$", "", RegexOptions.Multiline); //remove trailing sql or c++ comments
                s = Regex.Replace(s, @"\r\n([ \t]*/\*.*?\*/[ \t]*\r\n)+", "\r\n", RegexOptions.Singleline); //remove whole line c-like comments
                s = Regex.Replace(s, @"[ \t]*/\*.*?\*/[ \t]*", "", RegexOptions.Singleline); //remove trailing c-like comments
            }

            s = s.Trim().Replace("\t", "  "); //replace tabs with 2 spaces
            s = Regex.Replace(s, @" +$", "", RegexOptions.Multiline); //remove trailing whitespace
            s = Regex.Replace(s, "(\r\n){2,}", "\r\n"); //squeeze out multiple newlines
            if (!string.IsNullOrEmpty(indent)) s = Regex.Replace(s, @"^(.*)$", indent + "$1", RegexOptions.Multiline);  //indent
            return s;
        }

        public static bool IsNullOrEmpty(this string s) { return string.IsNullOrWhiteSpace(s); }

        public static int IndexOf<T>(this IList<T> list, Func<T, bool> match) where T : class
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (match(list[i])) return i;
            }
            return -1;
        }

        public static bool EqualsI(this string s, string value)
        {
            if (s == null && value == null) return true;
            return (s != null && value != null && s.Equals(value, StringComparison.InvariantCultureIgnoreCase));
        }

        public static bool ContainsI(this string s, string value)
        {
            if (s == null && value == null) return true;
            return (s != null && value != null && s.IndexOf(value, 0, StringComparison.InvariantCultureIgnoreCase) != -1);
        }

        public static string Attribute<T>(this Assembly asm) where T : Attribute
        {
            foreach (var data in asm.CustomAttributes)
            {
                if (typeof(T) != data.AttributeType) continue;
                if (data.ConstructorArguments.Count > 0) return data.ConstructorArguments[0].Value.ToString();
                break;
            }
            return string.Empty;
        }
    }

    public static class FormsExtensions
    {
        [DllImport("user32.dll")]
        private extern static IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
        private const int WM_SETREDRAW = 0x000B;

        public static void SuspendDrawing(this Control ctrl)
        {
            SendMessage(ctrl.Handle, WM_SETREDRAW, false, 0); //Stop redrawing
        }

        public static void ResumeDrawing(this Control ctrl)
        {
            SendMessage(ctrl.Handle, WM_SETREDRAW, true, 0);  //Turn on redrawing
            ctrl.Invalidate();
            ctrl.Refresh();
        }

        public static Rectangle ToParentRect(this Control parent, Control child)
        {
            var p = child.Parent;
            var rc = child.Bounds;
            while(p != null)
            {
                rc.X += p.Bounds.X;
                rc.Y += p.Bounds.Y;
                if (p == parent) break;
                p = p.Parent;
            }

            return rc;
        }

        public static T FindParent<T>(this Control ctl)
        {
            while (ctl != null)
            {
                if (ctl is T) return (T)(object)ctl;
                if (ctl.Tag is T) return (T)(object)ctl.Tag; //occurs with SummaryPopup
                ctl = ctl.Parent;
            }
            return default(T);
        }
    }

    public static class GDI
    {
        public static void DrawRoundedRectangle(this Graphics g, Pen p, Rectangle r, int d)
        {
            System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath();

            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.X + r.Width - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.X + r.Width - d, r.Y + r.Height - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Y + r.Height - d, d, d, 90, 90);
            gp.AddLine(r.X, r.Y + r.Height - d, r.X, r.Y + d / 2);

            g.DrawPath(p, gp);
        }

        public static void FillRoundedRectangle(this Graphics g, Brush b, Rectangle r, int d)
        {
            System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath();

            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.X + r.Width - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.X + r.Width - d, r.Y + r.Height - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Y + r.Height - d, d, d, 90, 90);
            gp.AddLine(r.X, r.Y + r.Height - d, r.X, r.Y + d / 2);

            g.FillPath(b, gp);
        }

        /// <summary>
        /// Create image from control.
        /// </summary>
        /// <param name="ctl">Control to create image from.</param>
        /// <param name="filename">Optional: Save image to file.</param>
        /// <param name="userComment">Optional: Embed comment into image</param>
        /// <returns>Created image</returns>
        public static Bitmap ToImage(this Control ctl, string filename = null, string userComment = null)
        {
            return ToImage(ctl, new Bitmap(ctl.Width, ctl.Height, PixelFormat.Format24bppRgb), new Rectangle(0, 0, ctl.Width, ctl.Height), filename, userComment);
        }

        /// <summary>
        /// Write image of control to existing bitmap buffer.
        /// </summary>
        /// <param name="ctl">Control to create image from.</param>
        /// <param name="targetBmp">Bitmap to write to</param>
        /// <param name="targetRect">What portion of target bitmap to write to.</param>
        /// <param name="filename">Optional: Save image to file.</param>
        /// <param name="userComment">Optional: Embed comment into image</param>
        /// <returns>Updated target bitmap</returns>
        public static Bitmap ToImage(this Control ctl, Bitmap targetBmp, Rectangle targetRect, string filename = null, string userComment = null)
        {
            const int ExifModel = 0x0110;
            const int ExifUserComment = 0x9286;
            const int ExifStringType = 2;

            //Bitmap bm = new Bitmap(ctl.Width, ctl.Height);
            ctl.DrawToBitmap(targetBmp, targetRect);

            if (!userComment.IsNullOrEmpty())
            {
                if (userComment[userComment.Length - 1] != '\0')
                    userComment = string.Concat(userComment, "\0");

                var pi = FormatterServices.GetUninitializedObject(typeof(PropertyItem)) as PropertyItem;
                pi.Id = ExifModel;
                pi.Type = ExifStringType;
                pi.Value = Encoding.UTF8.GetBytes("MediaGuide\0");
                pi.Len = pi.Value.Length;
                targetBmp.SetPropertyItem(pi);

                pi = FormatterServices.GetUninitializedObject(typeof(PropertyItem)) as PropertyItem;
                pi.Id = ExifUserComment;
                pi.Type = ExifStringType;
                pi.Value = Encoding.UTF8.GetBytes(userComment);
                pi.Len = pi.Value.Length;
                targetBmp.SetPropertyItem(pi);
            }

            if (!filename.IsNullOrEmpty())
            {
                ImageFormat iFormat;
                switch (Path.GetExtension(filename).ToLower())
                {
                    case ".bmp": iFormat = ImageFormat.Bmp; break;
                    case ".emf": iFormat = ImageFormat.Emf; break;
                    case ".gif": iFormat = ImageFormat.Gif; break;
                    case ".ico": iFormat = ImageFormat.Icon; break;
                    case ".jpg": iFormat = ImageFormat.Jpeg; break;
                    case ".png": iFormat = ImageFormat.Png; break;
                    case ".tif": iFormat = ImageFormat.Tiff; break;
                    case ".wmf": iFormat = ImageFormat.Wmf; break;
                    default: return targetBmp;
                }

                if (File.Exists(filename)) File.Delete(filename);
                targetBmp.Save(filename, iFormat);
            }

            return targetBmp;
        }

        /// <summary>
        /// Retrieve embedded user comment string from bitmap.
        /// </summary>
        /// <param name="bmp">Bitmap to retrieve user comment string from.</param>
        /// <returns>User comment string or null if not found</returns>
        public static string UserComment(this Bitmap bmp)
        {
            const int ExifUserComment = 0x9286;
            const int ExifStringType = 2;

            var prop = bmp.PropertyItems.FirstOrDefault(x => x.Id == ExifUserComment);
            if (prop != null && prop.Type == ExifStringType && prop.Value.Length > 0)
            {
                var value = Encoding.UTF8.GetString(prop.Value);
                if (value[value.Length - 1] == '\0') value = value.Substring(0, value.Length - 1);
                return value;
            }

            return null;
        }

        /// <summary>
        /// Resize the image to the specified width and height.
        /// </summary>
        /// <param name="image">The image to resize.</param>
        /// <param name="width">The width to resize to.</param>
        /// <param name="height">The height to resize to.</param>
        /// <returns>The resized image.</returns>
        public static Bitmap Resize(this Image image, int width, int height)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height, image.PixelFormat);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        /// <summary>
        /// Resize the image to the specified width and height.
        /// </summary>
        /// <param name="image">The image to resize.</param>
        /// <param name="nusize">The new dimensions resize to.</param>
        /// <returns>The resized image.</returns>
        public static Bitmap Resize(this Image image, Size nusize)
        {
            return Resize(image, nusize.Width, nusize.Height);
        }

        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private enum DeviceCap { VERTRES = 10, DESKTOPVERTRES = 117, LOGPIXELSY = 90 }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

        /// <summary>
        /// Get current DPI scaling factor as a percentage
        /// </summary>
        /// <returns>Scaling percentage</returns>
        public static int DpiScalingFactor()
        {
            IntPtr hDC = IntPtr.Zero;
            try
            {
                hDC = GetDC(IntPtr.Zero);
                int logpixelsy = GetDeviceCaps(hDC, (int)DeviceCap.LOGPIXELSY);
                float dpiScalingFactor = logpixelsy / 96f;
                //Smaller - 100% == screenScalingFactor=1.0 dpiScalingFactor=1.0
                //Medium - 125% (default) == screenScalingFactor=1.0 dpiScalingFactor=1.25
                //Larger - 150% == screenScalingFactor=1.0 dpiScalingFactor=1.5
                return (int)(dpiScalingFactor * 100f);
            }
            finally
            {
                if (hDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hDC);
            }
        }

        /// <summary>
        /// Blur an entire image by a scale value.
        /// </summary>
        /// <param name="image">Image to blur.</param>
        /// <param name="blurScale">A number greater 0.0 and less than 1.0</param>
        /// <returns>Blurred image</returns>
        public static Bitmap Blur(this Image original, double blurScale)
        {
            var b1 = new Bitmap(original, (int)(original.Width * blurScale), (int)(original.Height * blurScale));
            var b2 = new Bitmap(b1, original.Size);
            b1.Dispose();
            return b2;
        }

        [DllImport("gdiplus.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipLoadImageFromFile(string filename, out IntPtr image);
        [DllImport("gdiplus.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipDisposeImage(IntPtr image);
        private static readonly MethodInfo miFromGDIplus = typeof(Bitmap).GetMethod("FromGDIplus", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// Load image file into Bitmap object without any validation. Supposedly about 
        /// 3x faster than 'new Bitmap(filename);'. Does not support Windows EMF 
        /// metafiles. This uses the file as a cache thus uses less memory but more CPU.
        /// </summary>
        /// <param name="filename">Name of image file to load.</param>
        /// <returns>Loaded cached Bitmap object</returns>
        public static Bitmap FastLoadFromFile(string filename)
        {
            filename = Path.GetFullPath(filename);
            IntPtr loadingImage = IntPtr.Zero;

            var errorCode = GdipLoadImageFromFile(filename, out loadingImage);
            if (errorCode != 0)
            {
                if (loadingImage != IntPtr.Zero) GdipDisposeImage(loadingImage);
                throw new Win32Exception(errorCode, "GdipLoadImageFromFile: GDI+ threw a status error code.");
            }

            return (Bitmap)miFromGDIplus.Invoke(null, new object[] { loadingImage });
        }

        /// <summary>
        /// Loads entire image file into Bitmap object. This slurps up the entire file into memory.
        /// No caching. Less CPU but more memory. Cannot use low level GdipCreateBitmapFromStream() 
        /// because it uses internal GPStream class which in turn uses virtual methods that are not 
        /// implemented. Other 3rd-party image readers don't appear to be any faster.
        /// </summary>
        /// <param name="filename">Name of image file to load.</param>
        /// <returns>Loaded Bitmap object</returns>
        public static Bitmap FastLoadFromFileStream(string filename)
        {
            using (var fs = new FileStream(filename, FileMode.Open, System.Security.AccessControl.FileSystemRights.Read, FileShare.ReadWrite, 4096*8, FileOptions.RandomAccess))
            {
                return new Bitmap(fs);
            }
        }
    }
}