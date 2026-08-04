using System.Reflection;
using System.Runtime.InteropServices;
using DingLater.Core.Models;

namespace DingLater.App.Services;

internal static class DiagnosticsService
{
    internal static string Build(IEnumerable<CaptureHealth> health)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var lines = new List<string>
        {
            $"DingLater: {assembly}",
            $"OS: {RuntimeInformation.OSDescription}",
            $"Architecture: {RuntimeInformation.OSArchitecture}",
            $"Generated: {DateTimeOffset.Now:O}",
            "Runtime network: disabled by design",
            "Message content included: no"
        };
        lines.AddRange(health.Select(item =>
            $"Source[{item.SourceName}]: {item.State}; DatabaseFound={Format(item.DatabaseFound)}; KeyDerived={Format(item.KeyDerived)}; WalValid={Format(item.WalValid)}; SchemaCompatible={Format(item.SchemaCompatible)}; LastObservation={item.LastObservationAt:O}; Observed={item.ObservationCount}; Candidates={item.CandidateCount}; LastCapture={item.LastCaptureAt:O}; EmptyBodyCount={item.EmptyBodyCount}; ErrorCode={item.ErrorCode}; Updated={item.UpdatedAt:O}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "unknown"
    };
}
