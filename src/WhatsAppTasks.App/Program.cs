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
        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
        using var logger = new JsonlAppLogger(logsDirectory);
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
        {
            using var scope = logger.BeginScope(AppLogOperation.Ui);
            logger.Write(AppLogEvent.UiOperationFailed, exception: e.Exception);
            MessageBox.Show($"No se pudo completar la operación. Registro: {scope.CorrelationId}\nLogs: {logsDirectory}", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            using var scope = logger.BeginScope(AppLogOperation.Startup);
            logger.Write(AppLogEvent.StartupFailed, exception: e.ExceptionObject as Exception);
        };

        var repository = new SqliteTaskRepository(Path.Combine(dataDirectory, "WhatsAppTasks.db"));
        var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        var catalogHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        var sendingClient = new EvolutionClient(httpClient, repository, logger: logger);
        var catalogClient = new EvolutionClient(catalogHttpClient, repository, logger: logger);
        var executor = new MessageExecutor(repository, sendingClient, new AnthropicClient(httpClient, repository), new MessageSplitter(), new SystemAsyncDelay(), new ClaudeCodeRunner(), logger: logger);
        var scheduler = new BackgroundTaskScheduler(repository, executor, logger: logger);
        using var form = new MainForm(repository, scheduler, catalogClient, logger, logsDirectory);
        using var context = new ApplicationContext();
        form.ExitRequested += async (_, _) =>
        {
            try { await scheduler.StopAsync(); }
            catch (Exception error) { using var scope = logger.BeginScope(AppLogOperation.Scheduler); logger.Write(AppLogEvent.SchedulerFailed, exception: error); }
            context.ExitThread();
        };
        var showAtStartup = args.Any(x => string.Equals(x, "--show", StringComparison.OrdinalIgnoreCase));
        _ = form.Handle;
        form.BeginInvoke(async () =>
        {
            using var startupScope = logger.BeginScope(AppLogOperation.Startup);
            logger.Write(AppLogEvent.StartupStarted);
            try
            {
                try { await BootstrapSettingsAsync(repository); }
                catch (Exception error)
                {
                    logger.Write(AppLogEvent.StartupFailed, exception: error);
                    MessageBox.Show("No se pudo importar la configuración inicial. Puedes configurarla desde la pestaña Configuración.", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                await scheduler.StartAsync();
                await form.InitializeAsync();
                if (showAtStartup) form.ShowInterface();
                logger.Write(AppLogEvent.StartupCompleted);
            }
            catch (Exception error)
            {
                logger.Write(AppLogEvent.StartupFailed, exception: error);
                MessageBox.Show("No se pudo iniciar la aplicación. Revisa la configuración y vuelve a intentarlo.", "WhatsApp Tasks", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.RequestExit();
            }
        });
        Application.Run(context);
        scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        repository.DisposeAsync().AsTask().GetAwaiter().GetResult();
        httpClient.Dispose();
        catalogHttpClient.Dispose();
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
