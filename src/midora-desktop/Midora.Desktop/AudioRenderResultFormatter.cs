using System.Text;
using Midora.AudioRender;

namespace Midora.Desktop;

public static class AudioRenderResultFormatter
{
    private const int MaximumDiagnosticsPerScope = 12;

    public static string Format(AudioRenderTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        StringBuilder message = new();
        _ = message.AppendLine($"Status: {result.Status}")
            .AppendLine($"Succeeded: {result.SuccessCount}")
            .AppendLine($"Failed: {result.FailureCount}")
            .Append($"Elapsed: {result.Elapsed:g}");

        bool addedDetails = false;
        foreach (AudioRenderOutputResult output in result.Outputs.Where(value =>
                     value.Status is AudioRenderOutputStatus.Failed or AudioRenderOutputStatus.Cancelled))
        {
            _ = message.AppendLine().AppendLine()
                .AppendLine($"Output [{output.Status}]: {output.Target.FullPath}");
            int detailCount = 0;
            foreach (var diagnostic in output.CompilerDiagnostics.Take(MaximumDiagnosticsPerScope))
            {
                _ = message.AppendLine($"Compiler {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
                detailCount++;
            }
            foreach (AudioRenderDiagnostic diagnostic in output.Diagnostics.Take(MaximumDiagnosticsPerScope))
            {
                _ = message.AppendLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
                detailCount++;
            }
            if (detailCount == 0)
            {
                _ = message.AppendLine("No output diagnostic was returned.");
            }
            addedDetails = true;
        }

        if (result.Diagnostics.Count != 0)
        {
            _ = message.AppendLine().AppendLine().AppendLine("Task diagnostics:");
            foreach (AudioRenderDiagnostic diagnostic in result.Diagnostics.Take(MaximumDiagnosticsPerScope))
            {
                _ = message.AppendLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            }
            addedDetails = true;
        }

        if (!addedDetails && result.Status is AudioRenderTaskStatus.Failed or AudioRenderTaskStatus.CompletedWithErrors)
        {
            _ = message.AppendLine().AppendLine()
                .Append("No task or output diagnostic was returned.");
        }
        return message.ToString().TrimEnd();
    }
}
