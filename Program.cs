using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.ApplicationServices;

namespace Tingye
{
    internal sealed class Program : WindowsFormsApplicationBase
    {
        private ReaderWindow reader;

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args.Length > 0) args[0] = Path.GetFullPath(args[0]);
                new Program { IsSingleInstance = true }.Run(args);
            }
            catch (Exception ex)
            {
                MessageBox.Show("听页无法继续运行。\n\n" + ex.GetBaseException().Message, "听页", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnStartupNextInstance(StartupNextInstanceEventArgs args)
        {
            args.BringToForeground = false;
            if (reader != null) reader.RequestOpen(args.CommandLine.Count > 0 ? args.CommandLine[0] : null);
        }

        protected override bool OnStartup(Microsoft.VisualBasic.ApplicationServices.StartupEventArgs args)
        {
            bool firstInstance;
            // Keep the legacy mutex while users transition from the previous desktop build.
            using (var mutex = new Mutex(true, "Local\\Tingye.Desktop", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("听页已打开。可以在现有窗口中导入 EPUB。", "听页");
                    return false;
                }
                var app = new Application();
                Window window;
                using (var definition = typeof(Program).Assembly.GetManifestResourceStream("Tingye.MainWindow.xaml"))
                    window = (Window)XamlReader.Load(definition);
                using (var icon = typeof(Program).Assembly.GetManifestResourceStream("Tingye.Icon.ico"))
                    window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                string dataDirectory = Environment.GetEnvironmentVariable("TINGYE_DATA_DIR");
                if (String.IsNullOrWhiteSpace(dataDirectory))
                    dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tingye");
                reader = new ReaderWindow(window, new Library(dataDirectory), args.CommandLine.Count > 0 ? args.CommandLine[0] : null);
                app.Run(window);
            }
            return false;
        }
    }
}
