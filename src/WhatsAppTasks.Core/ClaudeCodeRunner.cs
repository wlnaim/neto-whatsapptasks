using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WhatsAppTasks.Core;

/// <summary>Runs `claude -p` as a child process limited by the task's allowed tools and timeout. The prompt goes through stdin.</summary>
public sealed class ClaudeCodeRunner : IClaudeCodeRunner
{
    private readonly string _executablePath;
    public ClaudeCodeRunner(string? executablePath = null) { _executablePath = executablePath ?? ResolveExecutable(); }

    public static string ResolveExecutable()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : "claude.exe";
    }

    public static IReadOnlyList<string> BuildArguments(string allowedTools)
    {
        var args = new List<string> { "-p", "--output-format", "json" };
        if (!string.IsNullOrWhiteSpace(allowedTools)) { args.Add("--allowedTools"); args.Add(allowedTools.Trim()); }
        return args;
    }

    public static GeneratedMessageResult ParseResult(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return new(false, "", "Claude Code returned no output.");
        try
        {
            using var doc = JsonDocument.Parse(stdout); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(false, "", "Claude Code returned an invalid response.");
            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True) return new(false, "", "Claude Code reported an error.");
            if (!root.TryGetProperty("subtype", out var subtype) || subtype.ValueKind != JsonValueKind.String || subtype.GetString() != "success") return new(false, "", "Claude Code did not finish successfully.");
            var text = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? new(false, "", "Claude Code returned an empty result.") : new(true, text, "");
        }
        catch (JsonException) { return new(false, "", "Claude Code returned an invalid response."); }
    }

    public Task<GeneratedMessageResult> RunAsync(string prompt, string workingDirectory, string allowedTools, int timeoutMinutes, CancellationToken cancellationToken = default) => RunAsync(prompt, workingDirectory, allowedTools, TimeSpan.FromMinutes(timeoutMinutes), cancellationToken);

    public async Task<GeneratedMessageResult> RunAsync(string prompt, string workingDirectory, string allowedTools, TimeSpan timeoutSpan, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)) return new(false, "", "The agent working directory does not exist.");
        var info = new ProcessStartInfo(_executablePath) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in BuildArguments(allowedTools)) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException) { return new(false, "", "The Claude Code executable could not be started."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(timeoutSpan);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            try { await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token); process.StandardInput.Close(); } catch (IOException) { /* the exit code decides the outcome */ }
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout.WaitAsync(timeout.Token); await stderr.WaitAsync(timeout.Token);
            return process.ExitCode != 0 ? new(false, "", "Claude Code exited with an error.") : ParseResult(output);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new(false, "", $"Claude Code timed out after {timeoutSpan.TotalMinutes:0.##} minutes.");
        }
    }
}
