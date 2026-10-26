using System.Threading;
using WhatsAppTasks.Core;

namespace WhatsAppTasks.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "WhatsAppTasks.App.SingleInstance", out var firstInstance);
        if (!firstInstance) return;
        var dataDirectory = GetOption(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsAppTasks");
        Directory.CreateDirectory(dataDirectory);
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, _) => MessageBox.Show("No se pudo completar la operación. Revisa la configuración y vuelve a intentarlo.", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Error);

        var repository = new SqliteTaskRepository(Path.Combine(dataDirectory, "WhatsAppTasks.db"));
        var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        var executor = new MessageExecutor(repository, new EvolutionClient(httpClient, repository), new AnthropicClient(httpClient, repository), new MessageSplitter(), new SystemAsyncDelay(), new ClaudeCodeRunner());
        var scheduler = new BackgroundTaskScheduler(repository, executor);
        using var form = new MainForm(repository, scheduler);
        using var context = new ApplicationContext();
        form.ExitRequested += async (_, _) =>
        {
            try { await scheduler.StopAsync(); }
            catch { /* closing must still terminate; errors are not shown from shutdown */ }
            context.ExitThread();
        };
        var showAtStartup = args.Any(x => string.Equals(x, "--show", StringComparison.OrdinalIgnoreCase));
        _ = form.Handle;
        form.BeginInvoke(async () =>
        {
            try
            {
                try { await BootstrapSettingsAsync(repository); }
                catch
                {
                    MessageBox.Show("No se pudo importar la configuración inicial. Puedes configurarla desde la pestaña Configuración.", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                await scheduler.StartAsync();
                await form.InitializeAsync();
                if (showAtStartup) form.ShowInterface();
            }
            catch
            {
                MessageBox.Show("No se pudo iniciar la aplicación. Revisa la configuración y vuelve a intentarlo.", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.RequestExit();
            }
        });
        Application.Run(context);
        scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        repository.DisposeAsync().AsTask().GetAwaiter().GetResult();
        httpClient.Dispose();
    }

    private static async Task BootstrapSettingsAsync(ITaskRepository repository)
    {
        const string collectionPath = @"C:\wrk\Personales\WhatsAppService\Evolution.postman_collection.json";
        const string anthropicKeyPath = @"C:\wrk\Personales\WhatsAppService\anthropic-api-key.txt";
        if (File.Exists(collectionPath))
            await SettingsBootstrapper.BootstrapEvolutionFromPostmanAsync(repository, collectionPath, anthropicKeyPath);
    }

    private static string? GetOption(string[] args, string name)
    {
        var item = args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return item is null ? null : item[(name.Length + 1)..];
    }
}
