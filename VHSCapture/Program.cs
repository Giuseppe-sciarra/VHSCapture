using System;
using System.IO;
using System.Windows.Forms;

namespace VHSCapture
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (o, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (o, e) => Crash(e.ExceptionObject as Exception);
            try { Application.Run(new MainForm()); }
            catch (Exception ex) { Crash(ex); }
        }

        static void Crash(Exception ex)
        {
            string text = DateTime.Now.ToString("s") + "\r\n" + (ex?.ToString() ?? "(null)") + "\r\n\r\n";
            string path = "";
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VHSCapture");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "crash.log");
                File.AppendAllText(path, text);
            }
            catch { }
            MessageBox.Show("VHSCapture ha avuto un errore:\r\n\r\n" + (ex?.Message ?? "?") + "\r\n\r\nDettagli in " + path, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
