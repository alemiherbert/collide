using Autodesk.Revit.DB;
using Collide.Analysis;
using Collide.Audit;
using Collide.Integration;
using Collide.Model;
using Collide.Monitoring;
using Collide.Resolution;
using Collide.Revit.Integration;
using Collide.Rules;
using Serilog;

namespace Collide.Revit.Application;

/// <summary>Everything Collide holds for one open project document.</summary>
internal sealed class CollideSession : IAsyncDisposable
{
    private CollideSession(Document document, RuleSet rules, string rulesSource, ILogger log)
    {
        Document = document;
        Rules = rules;
        RulesSource = rulesSource;
        Log = log;
        Analyzer = new ModelAnalyzer(logger: log);
        Engine = new IncrementalDetectionEngine(Analyzer, rules);
        Source = new RevitDeltaSource(document, log);
        Monitor = new LiveClashMonitor(Engine, Source, logger: log);
        var auditFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Collide", "audit");
        Directory.CreateDirectory(auditFolder);
        AuditPath = Path.Combine(auditFolder, $"{Sanitize(document.Title)}.audit.jsonl");
        Audit = new AuditLog(SystemClock.Instance, new JsonLinesAuditSink(AuditPath));
        Resolution = new ResolutionEngine(Analyzer, Audit, logger: log);
        Corrections = new CorrectionService(Engine, Resolution, () => new RevitModelEditor(document, log), new ExternalEventApiContext(), () => document.Application.Username);
        Interaction = new RevitModelInteraction(document, Corrections);
    }

    public Document Document { get; }
    public RuleSet Rules { get; }
    public string RulesSource { get; }
    public ILogger Log { get; }
    public ModelAnalyzer Analyzer { get; }
    public IncrementalDetectionEngine Engine { get; }
    public RevitDeltaSource Source { get; }
    public LiveClashMonitor Monitor { get; }
    public AuditLog Audit { get; }
    public string AuditPath { get; }
    public ResolutionEngine Resolution { get; }
    public CorrectionService Corrections { get; }
    public RevitModelInteraction Interaction { get; }

    public static CollideSession Start(Document document, ILogger log)
    {
        var (rules, source) = RulesProvider.Load(document, log);
        var session = new CollideSession(document, rules, source, log.ForContext("Document", document.Title));
        _ = session.Monitor.StartAsync();
        log.Information("Collide monitoring started for {Document} with rules {Rules} ({Source})", document.Title, rules.Name, source);
        return session;
    }

    public void OnChanged(ModelChange change) => Monitor.NotifyChanged(change);

    public ValueTask DisposeAsync() => Monitor.DisposeAsync();

    private static string Sanitize(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary>
/// Loads the project's rule configuration. Search order: COLLIDE_RULES environment variable, "&lt;model&gt;.collide.json"
/// or "collide.rules.json" next to the model, then %APPDATA%\Collide\collide.rules.json. Without a file, an empty rule set
/// is used: geometric clashes are still detected and every required compliance check is reported as indeterminate —
/// example values are never applied silently.
/// </summary>
internal static class RulesProvider
{
    public static (RuleSet Rules, string Source) Load(Document document, ILogger log)
    {
        foreach (var path in Candidates(document))
        {
            if (!File.Exists(path)) continue;
            try
            {
                return (RuleSetLoader.LoadFile(path), path);
            }
            catch (RuleConfigurationException ex)
            {
                log.Error("Rule configuration {Path} is invalid: {Errors}", path, string.Join("; ", ex.Errors));
                return (Empty($"Invalid rule file {path}"), path + " (invalid; see log)");
            }
        }

        return (Empty("No project rules configured"), "none");
    }

    private static IEnumerable<string> Candidates(Document document)
    {
        if (Environment.GetEnvironmentVariable("COLLIDE_RULES") is { Length: > 0 } env) yield return env;
        if (!string.IsNullOrEmpty(document.PathName) && Path.GetDirectoryName(document.PathName) is { } folder)
        {
            yield return Path.Combine(folder, Path.GetFileNameWithoutExtension(document.PathName) + ".collide.json");
            yield return Path.Combine(folder, "collide.rules.json");
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Collide", "collide.rules.json");
    }

    private static RuleSet Empty(string name) => new()
    {
        Name = name,
        DesignStandard = new DesignStandardInfo { Name = "(none)", Notes = "No rules loaded: compliance checks are indeterminate." },
    };
}
