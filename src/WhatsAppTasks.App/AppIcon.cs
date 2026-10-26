namespace WhatsAppTasks.App;

internal static class AppIcon
{
    private const string ResourceName = "WhatsAppTasks.App.WhatsAppTasks.ico";

    public static Icon Create()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("No se encontró el icono de la aplicación.");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
