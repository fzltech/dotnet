using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.HighPerformance.Conformance;
using Xunit;

namespace CommunityToolkit.HighPerformance.Verification;

public class SymbolicMethodChecks
{
    [Fact]
    public void Annotated_methods_satisfy_contracts_and_do_not_overflow()
    {
        var targetSource = TargetSourceDirectory();
        if (!System.IO.Directory.Exists(targetSource))
        {
            ClearCoverage();
            return;
        }

        var results = SymbolicMethodChecker.CheckAllWithLocations(targetSource);

        // Overwrite on every run, including a run that finds nothing: a stale file from an earlier
        // run must never read as this run's measurement.
        WriteCoverage(results, targetSource);

        if (results.Count == 0)
            return;

        var failures = results.Where(r => r.Result is not SymbolicCheckResult.Verified).ToList();
        Assert.True(failures.Count == 0, FormatFailures(failures.Select(r => r.Result).ToList()));
    }

    /// <summary>
    /// Removes a previous run's report when this run cannot measure anything at all. Absence means
    /// "not measured"; a stale file would be read as this run's result.
    /// </summary>
    private static void ClearCoverage()
    {
        DeleteIfSet("FMVERIFY_SYMBOLIC_OUT");
        DeleteIfSet("FMVERIFY_SYMBOLIC_TEXT_OUT");
    }

    private static void DeleteIfSet(string variable)
    {
        var path = System.Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path))
            return;

        try { System.IO.File.Delete(path); }
        catch (IOException) { }                        // Report-only: never change the verdict.
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (System.Security.SecurityException) { }
    }

    /// <summary>
    /// Publishes what the run found for the audit. Report-only by construction: an unwritable
    /// output path is ignored, so a reporting artefact can never turn a layer whose proofs are
    /// fine red. verify.sh sets FMVERIFY_SYMBOLIC_OUT to symbolic-coverage.json and
    /// FMVERIFY_SYMBOLIC_TEXT_OUT to symbolic-coverage.txt; an unset variable means "do not write
    /// that file".
    /// </summary>
    private static void WriteCoverage(
        IReadOnlyList<LocatedSymbolicResult> results, string targetSourceDirectory)
    {
        var repositoryRoot = RepositoryRoot();

        var jsonPath = System.Environment.GetEnvironmentVariable("FMVERIFY_SYMBOLIC_OUT");
        if (!string.IsNullOrEmpty(jsonPath))
        {
            try
            {
                System.IO.File.WriteAllText(
                    jsonPath, CoverageJson(results, repositoryRoot, targetSourceDirectory));
            }
            catch (IOException) { }                        // Report-only: never change the verdict.
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (System.Security.SecurityException) { }
        }

        var textPath = System.Environment.GetEnvironmentVariable("FMVERIFY_SYMBOLIC_TEXT_OUT");
        if (!string.IsNullOrEmpty(textPath))
        {
            try
            {
                System.IO.File.WriteAllText(
                    textPath, CoverageText(results, targetSourceDirectory));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    private static string CoverageJson(
        IReadOnlyList<LocatedSymbolicResult> results,
        string? repositoryRoot,
        string targetSourceDirectory)
    {
        var entries = results.Select(result =>
        {
            var (outcome, reason) = Outcome(result.Result);
            var file = (repositoryRoot is null
                    ? result.Location.File
                    : System.IO.Path.GetRelativePath(repositoryRoot, result.Location.File))
                .Replace('\\', '/');

            return "    { \"method\": " + JsonString(result.Location.Owner) +
                   ", \"signature\": " + JsonString(result.Location.Signature) +
                   ", \"file\": " + JsonString(file) +
                   ", \"line\": " + result.Location.Line +
                   ", \"outcome\": " + JsonString(outcome) +
                   ", \"reason\": " + JsonString(reason) + " }";
        }).ToList();

        var lines = new List<string>
        {
            "{",
            """  "schemaVersion": 1,""",
            "  \"found\": " + entries.Count + ",",
        };

        if (entries.Count == 0)
        {
            lines.Add("""  "results": [],""");
            lines.Add("  \"warning\": " + JsonString(NoMethodsWarning(targetSourceDirectory)));
        }
        else
        {
            lines.Add("""  "results": [""");
            lines.Add(string.Join("," + Environment.NewLine, entries));
            lines.Add("  ]");
        }

        lines.Add("}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string CoverageText(
        IReadOnlyList<LocatedSymbolicResult> results, string targetSourceDirectory)
    {
        var verified = results.Count(result => result.Result is SymbolicCheckResult.Verified);
        var unsupported = results.Count(result => result.Result is SymbolicCheckResult.Unsupported);
        var vacuous = results.Count(result => result.Result is SymbolicCheckResult.PreconditionContradiction);
        var failed = results.Count(result =>
            result.Result is SymbolicCheckResult.ContractViolation or SymbolicCheckResult.OverflowDivergence);

        var lines = new List<string>
        {
            $"Symbolic coverage: {results.Count} annotated methods; " +
            $"{verified} verified, {unsupported} unsupported, {vacuous} vacuous, {failed} failed."
        };

        if (results.Count == 0)
        {
            lines.Add("warning: " + NoMethodsWarning(targetSourceDirectory));
        }
        else
        {
            foreach (var located in results.Where(result => result.Result is not SymbolicCheckResult.Verified))
            {
                var (outcome, reason) = Outcome(located.Result);
                lines.Add($"[{outcome}] {located.Location.Owner} {located.Location.Signature}: {reason}");
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string NoMethodsWarning(string targetSourceDirectory) =>
        $"no contract-annotated methods found in {targetSourceDirectory}";

    private static (string Outcome, string? Reason) Outcome(SymbolicCheckResult result) => result switch
    {
        SymbolicCheckResult.Verified => ("verified", null),
        SymbolicCheckResult.ContractViolation violation => ("violation", violation.ViolatedEnsures),
        SymbolicCheckResult.OverflowDivergence => ("overflow", null),
        SymbolicCheckResult.PreconditionContradiction contradiction =>
            ("vacuous", contradiction.Precondition),
        SymbolicCheckResult.Unsupported unsupported => ("unsupported", unsupported.Reason),
        _ => ("unsupported", result.ToString()),
    };

    /// <summary>
    /// The repository root as the audit understands it: the parent of the directory that holds the
    /// generated solution. The file field is relative to this, with forward slashes.
    /// </summary>
    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.Parent?.FullName;
    }

    private static string JsonString(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    private static string FormatFailures(IReadOnlyList<SymbolicCheckResult> failures)
    {
        var lines = failures.Select(f => f switch
        {
            SymbolicCheckResult.ContractViolation cv =>
                $"[ContractViolation] {cv.MethodName}: violated postcondition '{cv.ViolatedEnsures}'\n" +
                $"  Inputs: {FormatMap(cv.Inputs)}\n" +
                $"  Actual Result: {cv.ActualResult}",
            SymbolicCheckResult.OverflowDivergence od =>
                $"[OverflowDivergence] {od.MethodName}: 32-bit machine arithmetic diverges from mathematical arithmetic\n" +
                $"  Inputs: {FormatMap(od.Inputs)}\n" +
                $"  Real Result: {od.RealResult}\n" +
                $"  Ideal Result: {od.IdealResult}",
            SymbolicCheckResult.PreconditionContradiction pc =>
                $"[PreconditionContradiction] {pc.MethodName}: precondition '{pc.Precondition}' is unsatisfiable (contradiction / vacuous)",
            SymbolicCheckResult.Unsupported u =>
                $"[Unsupported] {u.MethodName}: {u.Reason}",
            _ => f.ToString() ?? "Unknown failure"
        });

        return $"Symbolic verification found {failures.Count} defect(s):\n\n" + string.Join("\n\n", lines);
    }

    private static string FormatMap(IReadOnlyDictionary<string, object> map) =>
        string.Join(", ", map.Select(kv => $"{kv.Key} = {kv.Value}"));

    private static string TargetSourceDirectory()
    {
        var relPath = @"../../../src/CommunityToolkit.HighPerformance"
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .Replace('/', System.IO.Path.DirectorySeparatorChar);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.csproj")) && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.csproj")))
            {
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, relPath));
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
            else if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.slnx")))
            {
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, "test", "CommunityToolkit.HighPerformance.Verification", relPath));
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
        }

        var fromCurrent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), relPath));
        if (System.IO.Directory.Exists(fromCurrent)) return fromCurrent;

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, relPath));
    }
}
