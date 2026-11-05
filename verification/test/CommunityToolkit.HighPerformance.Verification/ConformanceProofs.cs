using System.Runtime.CompilerServices;
using CommunityToolkit.HighPerformance.Conformance;
using CommunityToolkit.HighPerformance.Spec;
using Xunit;

namespace CommunityToolkit.HighPerformance.Verification;

/// <summary>
/// Hands this project's checks to Spec as the gate assembly loads, which is before any test in it
/// runs. A module initializer rather than a fixture: the earned-backing assertion lives in
/// RegistryProofs, and nothing may depend on which test executes first.
/// </summary>
internal static class ConformanceEvidenceRegistration
{
    [ModuleInitializer]
    internal static void Register() => ConformanceEvidence.Register(new ConformanceRegistry().Checks);
}

/// <summary>
/// The half of the gate that can say something about the real code.
///
/// <para>The earned-backing rule itself is asserted once, in <c>RegistryProofs</c>, against the
/// checks registered above — one rule, one assertion. What this file adds is the part that rule
/// depends on: that every registered check actually passes, and that a property claiming the
/// stronger backing records what its run covered.</para>
/// </summary>
public class ConformanceProofs
{
    private static readonly ICapabilityRegistry Registry = new CapabilityRegistry();

    // Read back from the seam rather than constructed again, so this asserts on the very
    // checks RegistryProofs judges the backing against.
    private static IReadOnlyList<ConformanceCheck> Checks => ConformanceEvidence.Checks;

    [Fact]
    public void Every_registered_conformance_check_passes()
    {
        // One test over every check rather than one test per check. A data-driven test cannot say
        // "no rows is fine", and no rows is exactly the state a freshly bridged layer is in — which
        // would take the gate red before the adopter had written anything. An empty registry is not
        // a failure here; RegistryProofs is what refuses a backing claimed without evidence.
        //
        // It also reports every failing check rather than stopping at the first, which is what you
        // want when one model change breaks several at once.
        //
        // Every report, not only the failing ones: annotation coverage is computed across the whole
        // set, and filtering first would throw away exactly the reports that prove an annotation was
        // reached by something.
        var reports = Checks.Select(c => (c.PropertyId, Report: c.Run())).ToArray();

        // Before the assertion, so a red run still produces the file. Coverage is most useful on the
        // run that also failed, and an assertion that throws first would withhold it there.
        WriteCoverage(reports.Select(x => x.Report));

        var failures = reports.Where(x => !x.Report.Passed).ToArray();

        Assert.True(failures.Length == 0, string.Join("\n\n", failures.Select(f =>
            $"conformance check for '{f.PropertyId}' did not pass " +
            $"({f.Report.BoundDescription}):\n" +
            string.Join("\n", f.Report.Failures.Select(x => $"  [{x.Kind}] {x.Scenario}: {x.Detail}")))));
    }

    [Fact]
    public void Every_conformance_backed_property_records_what_its_run_covered()
    {
        // "Conformance checked" means nothing without "over how many scenarios, and was that all of
        // them". A backed property whose scope does not say is a bounded claim presented as a
        // complete one, and the bound is the first thing a reader of a green gate needs.
        var unrecorded = Registry.AlreadyEncoded
            .Where(p => p.Backing == TrustBacking.ConformanceBacked)
            .Where(p => string.IsNullOrWhiteSpace(p.Scope?.Bound))
            .Select(p => p.Id)
            .ToArray();

        Assert.True(unrecorded.Length == 0,
            "conformance-backed but no Scope.Bound recording the run: " + string.Join(", ", unrecorded) +
            "\nConformanceReport.BoundDescription is written for this.");
    }

    [Fact]
    [Trait("Category", "hunt")]
    public void Every_registered_hunt_is_reported()
    {
        // Excluded from the gate by verify.sh: a hunt draws a fresh seed, so running it here would
        // make the gate red on Tuesday and green on Wednesday. Run it deliberately with
        // --filter Category=hunt.
        //
        // A failure here is a finding, not a broken build. Pin the seed it names into Checks and it
        // becomes a regression test the gate keeps for you.
        var failures = new ConformanceRegistry().Hunts
            .Select(h => (h.PropertyId, Report: h.Run()))
            .Where(x => !x.Report.Passed)
            .ToArray();

        Assert.True(failures.Length == 0, string.Join("\n\n", failures.Select(f =>
            $"hunt for '{f.PropertyId}' found a disagreement " +
            $"({f.Report.BoundDescription}):\n" +
            string.Join("\n", f.Report.Failures.Select(x => $"  [{x.Kind}] {x.Scenario}: {x.Detail}")))));
    }

    /// <summary>
    /// Writes annotation coverage where <c>verify.sh</c> can print it, and nowhere otherwise.
    ///
    /// <para>Opt-in through the environment, because the gate prints only failures: on a green run —
    /// the run this is for — nothing reaches a reader, and a passing test's output is hidden at
    /// default verbosity. So <c>verify.sh</c> names a path and prints the file afterwards, and an
    /// adopter running <c>dotnet test</c> directly gets no stray artefact.</para>
    ///
    /// <para><b>This never fails the gate.</b> Coverage is a fact about what the run reached, and
    /// zero-applied is ambiguous between a misconfigured bridge and a slice deliberately left
    /// unannotated — nothing here can tell them apart, so nothing here may judge. An unwritable path
    /// is silently ignored for the same reason: a reporting convenience must not be able to redden a
    /// layer whose proofs are fine.</para>
    /// </summary>
    private static void WriteCoverage(IEnumerable<ConformanceReport> reports)
    {
        var path = Environment.GetEnvironmentVariable("FMVERIFY_COVERAGE_OUT");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            // Materialised: both describers enumerate, and a lazily-projected sequence of reports
            // would re-run every registered check for the second one.
            var all = reports.ToList();
            var blocks = new[] { LayerAnnotationCoverage.Describe(all), LayerEncodingCoverage.Describe(all) }
                .Where(b => !string.IsNullOrEmpty(b));

            File.WriteAllText(path, string.Join(Environment.NewLine, blocks) + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
