using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// The unbounded engine: runs the pinned Dafny on one <see cref="UnboundedProperty"/> at a time and
/// turns what it reports into a <see cref="VcResult"/> under the same rule as the solver —
/// <see cref="Outcome.Proven"/> only for a clean proof, and anything short of one a failure.
///
/// <para>Generated into every layer and run only by one that lists a claim. Dafny itself is fetched
/// into <c>.tools/</c> by <c>fetch-dafny.sh</c>, which <c>verify.sh</c> runs when the layer opted in;
/// it is never looked up on <c>PATH</c>, so the version that proved a claim is the pinned one.</para>
/// </summary>
public static class DafnyEngine
{
    public const string PinnedVersion = "4.11.0";

    /// <summary>
    /// Wall-clock limit for one claim. Dafny has per-obligation limits of its own; this one exists so
    /// a run that hangs fails the gate instead of stalling it, and a claim it cuts off is
    /// <see cref="Outcome.Unknown"/>, which is failure.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(5);

    public static VcResult Verify(UnboundedProperty property)
    {
        var name = $"Dafny[{property.Id}]";

        var root = LocateScaffoldRoot();
        if (root is null)
            return new VcResult(name, Outcome.Unknown, "No scaffold root (CommunityToolkit.HighPerformance.Verification.slnx) above the running assembly.");

        var executable = Executable(root);
        if (executable is null)
            return new VcResult(name, Outcome.Unknown,
                $"Dafny {PinnedVersion} is not installed under .tools/. Run fetch-dafny.sh or fetch-dafny.ps1 " +
                "(verify.sh does this when the layer opted into the unbounded engine).");

        var source = Path.Combine(root, property.DafnyFile);
        if (!File.Exists(source))
            return new VcResult(name, Outcome.Unknown, $"{property.DafnyFile} does not exist.");

        // Dafny's filter matches any symbol whose qualified name contains the text, so a member that
        // does not exist could still verify something else. Requiring the declaration closes that.
        if (!Declares(File.ReadAllText(source), property.Member))
            return new VcResult(name, Outcome.Unknown, $"{property.DafnyFile} declares no member named '{property.Member}'.");

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("verify");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add("--filter-symbol");
        // A trailing dot anchors the match at the end of the qualified name, so `Max` does not also
        // select `MaxOf`.
        start.ArgumentList.Add(property.Member + ".");

        using var process = Process.Start(start);
        if (process is null)
            return new VcResult(name, Outcome.Unknown, $"{executable} could not be started.");

        // Both streams drained concurrently: waiting for exit first deadlocks as soon as either pipe
        // fills, which a failing proof's output easily does.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)TimeLimit.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new VcResult(name, Outcome.Unknown, $"Dafny did not finish within {TimeLimit.TotalMinutes} minutes.");
        }

        var output = stdout.Result + stderr.Result;
        var outcome = Classify(process.ExitCode, output);

        // Dafny's own report stands in for a counterexample: it names the failing postcondition and
        // the path that reaches it, which is what a reader of a red gate needs next.
        return new VcResult(name, outcome, outcome == Outcome.Proven ? null : output);
    }

    /// <summary>
    /// Proven only on exit 0 with a summary that verified at least one symbol and reported nothing
    /// else. Dafny appends time outs, inconclusive results, resource and memory exhaustion and solver
    /// exceptions to the summary, and each of those is a claim that was not proven: the same rule
    /// that makes <c>UNKNOWN</c> from Z3 a failure.
    /// </summary>
    private static Outcome Classify(int exitCode, string output)
    {
        var summary = Summary.Match(output);
        if (!summary.Success)
            return Outcome.Unknown;

        var verified = int.Parse(summary.Groups["verified"].Value);
        var errors = int.Parse(summary.Groups["errors"].Value);
        var unfinished = summary.Groups["rest"].Value.Length > 0;

        if (errors > 0) return Outcome.Refuted;
        if (exitCode != 0 || unfinished || verified == 0) return Outcome.Unknown;
        return Outcome.Proven;
    }

    private static bool Declares(string source, string member) =>
        Regex.IsMatch(
            source,
            $@"\b(method|lemma|function|predicate|least\s+lemma|greatest\s+lemma|twostate\s+lemma)\s+(\{{:[^}}]*\}}\s*)*{Regex.Escape(member)}\b");

    private static string? Executable(string root)
    {
        var directory = Path.Combine(root, ".tools", $"dafny-{PinnedVersion}", "dafny");
        var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "Dafny.exe" : "dafny");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? LocateScaffoldRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommunityToolkit.HighPerformance.Verification.slnx")))
                return dir.FullName;
        }
        return null;
    }

    // Not [GeneratedRegex]: this project builds for the target's framework, which may predate it.
    private static readonly Regex Summary =
        new(@"finished with (?<verified>\d+) verified, (?<errors>\d+) errors?(?<rest>[^\r\n]*)");
}
