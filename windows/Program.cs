namespace HBProxy.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // WinDivert resolves its signed driver beside the application binaries.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        ApplicationConfiguration.Initialize();

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: "Local\\Bodex.HBProxy.WindowsClient",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "H.B Proxy is already running.",
                "H.B Proxy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

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
