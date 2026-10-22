using System.Windows;
using System.Windows.Threading;

namespace BIMUP.CDE;

public partial class App : Application
{
    public App()
    {
        System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", $"App ctor (PID {Environment.ProcessId})\n");
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "Unhandled: " + e.ExceptionObject + "\n");
        };
        Exit += (s, e) =>
        {
            System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", $"App Exit event fired with code {e.ApplicationExitCode}, stack:\n{Environment.StackTrace}\n");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "OnStartup begin\n");
            base.OnStartup(e);
            System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "OnStartup base done\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "OnStartup ex: " + ex + "\n");
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.IO.File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "DispatcherUnhandled: " + e.Exception + "\n");
        MessageBox.Show(
            $"Đã xảy ra lỗi không mong muốn:\n{e.Exception.Message}",
            "BIMUP.CDE",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
