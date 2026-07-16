namespace SshProxyBridge.App;

internal sealed record PowerShellOutput(
    string StandardOutput,
    string StandardError,
    bool TimedOut);

internal static class PowerShellOutputDrain
{
    private const string TimeoutWarning =
        "[WARN] 后台命令已经结束，但子进程仍占用输出通道；GUI 已停止等待该通道。";

    internal static async Task<PowerShellOutput> CompleteAsync(
        Task<string> standardOutputTask,
        Task<string> standardErrorTask,
        CancellationTokenSource readCancellation,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(standardOutputTask);
        ArgumentNullException.ThrowIfNull(standardErrorTask);
        ArgumentNullException.ThrowIfNull(readCancellation);

        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(timeout);
            return new PowerShellOutput(
                await standardOutputTask,
                await standardErrorTask,
                TimedOut: false);
        }
        catch (TimeoutException)
        {
            readCancellation.Cancel();
            ObserveLateFault(standardOutputTask);
            ObserveLateFault(standardErrorTask);

            var standardOutput = CompletedValueOrEmpty(standardOutputTask);
            var standardError = AppendWarning(
                CompletedValueOrEmpty(standardErrorTask),
                TimeoutWarning);

            return new PowerShellOutput(standardOutput, standardError, TimedOut: true);
        }
    }

    private static string CompletedValueOrEmpty(Task<string> task) =>
        task.IsCompletedSuccessfully ? task.Result : string.Empty;

    private static string AppendWarning(string existing, string warning) =>
        string.IsNullOrWhiteSpace(existing)
            ? warning
            : $"{existing.TrimEnd()}{Environment.NewLine}{warning}";

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
