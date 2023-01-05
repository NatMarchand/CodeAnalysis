using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

MSBuildLocator.RegisterDefaults();

using var workspace = MSBuildWorkspace.Create();
var progress = new Progress<ProjectLoadProgress>(p => Console.WriteLine($"[{p.ElapsedTime:g}] {p.FilePath} {p.Operation}"));
var solution = await workspace.OpenSolutionAsync(args[0], progress);
var solutionDirectory = Directory.GetParent(solution.FilePath!)!.FullName;
var allDiagnostics = new List<object>();

foreach (var project in solution.Projects)
{
    var compilation = await project.GetCompilationAsync();
    allDiagnostics.AddRange(compilation!.GetDiagnostics().Where(Filter).Select(Project));
    
    var analyzers = project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(project.Language)).ToImmutableArray();
    if (analyzers.Length == 0)
        continue;

    var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, project.AnalyzerOptions, CancellationToken.None);
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    allDiagnostics.AddRange(diagnostics.Where(Filter).Select(Project));
}

bool Filter(Diagnostic diag)
{
    return diag.Severity != DiagnosticSeverity.Hidden && diag.Location.Kind == LocationKind.SourceFile;
}

object Project(Diagnostic diag)
{
    var lineSpan = diag.Location.GetLineSpan();
    return new
    {
        Description = $"{diag.Id}: {diag.GetMessage(CultureInfo.InvariantCulture)}",
        Severity = diag.Severity switch
        {
            DiagnosticSeverity.Error => "blocker",
            DiagnosticSeverity.Warning => "major",
            DiagnosticSeverity.Info => "info"
        },
        Location = new
        {
            Path = Path.GetRelativePath(solutionDirectory ,diag.Location.SourceTree.FilePath),
            Lines = new
            {
                Begin = lineSpan.StartLinePosition.Line + 1,
                End = lineSpan.EndLinePosition.Line + 1
            }
        },
        Fingerprint = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{diag.Location.SourceTree.FilePath}:{diag.Location.SourceSpan}:{diag.Id}")))
    };
}

await using var outputStream = File.Open("code_analysis.json", FileMode.Create);
await JsonSerializer.SerializeAsync(outputStream, allDiagnostics, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });