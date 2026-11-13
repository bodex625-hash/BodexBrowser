namespace HBProxy.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            // Recover firewall/routes left by a forced termination before
            // allowing normal desktop use. A failed recovery stays fail-closed.
            WindowsNetworkRecovery.RecoverAbandonedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "H.B Proxy could not safely recover the previous network session. " +
                "The application will not continue because doing so could expose traffic.\n\n" +
                ex.Message,
                "H.B Proxy network recovery",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm());
    }
}
