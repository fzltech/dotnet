using System.Text.Json;
using CommunityToolkit.HighPerformance.Spec;
using Xunit;

namespace CommunityToolkit.HighPerformance.Verification;

/// <summary>
/// The registry gate: every property the project declares encoded must have ALL of
/// its VCs Proven. New properties enrol automatically by appearing in the registry.
/// </summary>
public class RegistryProofs
{
    private static readonly ICapabilityRegistry Registry = new CapabilityRegistry();
    private static readonly Verifier V = new();

    // Yields the id rather than the property, so the test's display name is just the id. Passing
    // the PropertyDefinition makes xUnit render the whole record into the name — delegates, scope
    // and all, some 700 characters of it — which buries the per-property duration the gate reports
    // and is the first thing anyone reads when a proof goes red or gets slow.
    public static IEnumerable<object[]> AllProperties() =>
        Registry.Properties.Select(p => new object[] { p.Id });

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void Every_encoded_property_is_fully_proven(string propertyId)
    {
        var p = Registry.Properties.Single(candidate => candidate.Id == propertyId);

        foreach (var r in V.VerifyAll(p))
            Assert.True(r.Outcome == Outcome.Proven,
                $"VC '{r.Name}' was {r.Outcome}." +
                (r.Counterexample is null ? "" : $"\nCounterexample:\n{r.Counterexample}"));
    }

    // One test over every claim rather than a theory per claim: xUnit fails a theory whose data is
    // empty, and a layer that has not opted in has no unbounded claims at all. Every failure is
    // collected, so one red claim does not hide the next.
    [Fact]
    public void Every_unbounded_property_is_proven_by_its_engine()
    {
        var failures = UnboundedRegistry.Properties
            .Select(p => DafnyEngine.Verify(p))
            .Where(r => r.Outcome != Outcome.Proven)
            .Select(r => $"'{r.Name}' was {r.Outcome}." + (r.Counterexample is null ? "" : $"\n{r.Counterexample}"))
            .ToArray();

        Assert.True(failures.Length == 0, string.Join("\n\n", failures));
    }

    [Fact]
    public void Registry_declares_at_least_one_property()
        => Assert.NotEmpty(Registry.Properties);

    [Fact]
    public void No_property_claims_a_trust_backing_the_layer_cannot_justify()
    {
        // The only assertion of this rule. Evidence arrives through ConformanceEvidence, which the
        // conformance gate registers as its assembly loads; with no bridge nothing registers, the
        // list is empty, and every ConformanceBacked claim is rejected — which is the right answer
        // for a layer that cannot check one.
        var unjustified = TrustBoundary.UnjustifiedBackings(Registry, ConformanceEvidence.Checks);

        Assert.True(unjustified.Count == 0,
            "ConformanceBacked needs a conformance check that passes, and is never available to a " +
            "termination or an unbounded claim, which no finite run can witness. Refused for: " +
            string.Join(", ", unjustified) +
            ". Run `fmverify add-bridge` and register a check, or declare ModelOnly.");
    }

    [Fact]
    public void No_property_names_an_engine_that_did_not_discharge_it()
    {
        var misattributed = TrustBoundary.MisattributedEngines(Registry);

        Assert.True(misattributed.Count == 0,
            "A property names a proof engine that did not discharge it: " +
            string.Join(", ", misattributed) +
            ". A PropertyDefinition is proven by the bounded Verifier, and an encoded property " +
            "takes the engine of its class.");
    }

    [Fact]
    public void The_committed_trust_boundary_matches_the_registry()
    {
        var committed = File.ReadAllText(TrustBoundary.LocateFile());
        var rendered = TrustBoundary.Render(Registry);

        Assert.True(TrustBoundary.Matches(committed, rendered),
            "TRUST-BOUNDARY.md is stale. Regenerate it:\n" +
            "  dotnet run --project src/CommunityToolkit.HighPerformance.Spec.Descriptor -- --emit-trust-boundary\n\n" +
            "Rendered document follows, for pasting into the template if you are changing Render:\n" +
            rendered);
    }

    [Fact]
    public void Every_translation_claim_is_verified()
    {
        var evidenceFile = TrustBoundary.LocateTranslationEvidence();
        if (evidenceFile is null || !File.Exists(evidenceFile))
            return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(evidenceFile));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The message has to name the file: "translation-evidence.json is malformed" is
            // actionable, "Expected depth to be zero" is not.
            Assert.True(false, $"Could not read translation evidence '{evidenceFile}': {ex.Message}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var summary = root.GetProperty("summary");
            var failed = summary.GetProperty("failed").GetInt32();
            var notRun = summary.GetProperty("notRun").GetInt32();
            var unknown = summary.TryGetProperty("unknown", out var pUnknown)
                ? pUnknown.GetInt32()
                : 0;
            var mathematicalOnly = summary.TryGetProperty("mathematicalOnly", out var pMath)
                ? pMath.GetInt32()
                : 0;
            var partial = summary.TryGetProperty("partial", out var pPartial)
                ? pPartial.GetInt32()
                : 0;
            var vacuous = summary.TryGetProperty("vacuous", out var pVacuous)
                ? pVacuous.GetInt32()
                : 0;

            // The claim's own status, not just the counters: a summary and a claims array that
            // disagree is the state a hand edit or a badly resolved merge leaves behind, and the
            // published claim — not the count — is the record that has to be proven.
            //
            // Semantics and termination are load-bearing: a mathematical-int proof is about
            // unbounded integers, not C# int, and a partial proof is not a total-correctness proof.
            var failedArtifacts = root.GetProperty("artifacts").EnumerateArray()
                .Where(a => a.GetProperty("status").GetString() != "verified")
                .Select(a => $"{a.GetProperty("output").GetString()}: {a.GetProperty("status").GetString()}")
                .ToArray();

            var unverified = root.GetProperty("claims").EnumerateArray()
                .Where(c =>
                {
                    var status = c.GetProperty("status").GetString();
                    var semantics = c.TryGetProperty("semantics", out var pSem) ? pSem.GetString() : null;
                    var termination = c.TryGetProperty("termination", out var pTerm) ? pTerm.GetString() : "unknown";
                    var isVacuous = c.TryGetProperty("vacuous", out var pVac) && pVac.GetBoolean();
                    return status != "verified" || semantics != "int32-bitvector" ||
                           termination != "total" || isVacuous;
                })
                .Select(c =>
                {
                    var status = c.GetProperty("status").GetString();
                    var semantics = c.TryGetProperty("semantics", out var pSem) ? pSem.GetString() ?? "" : "";
                    var termination = c.TryGetProperty("termination", out var pTerm) ? pTerm.GetString() ?? "unknown" : "unknown";
                    var isVacuous = c.TryGetProperty("vacuous", out var pVac) && pVac.GetBoolean();
                    var display = status;
                    if (display == "verified")
                    {
                        if (isVacuous) display = "vacuous";
                        else if (semantics != "int32-bitvector") display = "mathematical-only";
                        else if (termination != "total") display = termination;
                    }
                    return $"{c.GetProperty("id").GetString()}: {display} " +
                           $"({c.GetProperty("owner").GetString()}, {semantics}, {termination})";
                })
                .ToArray();

            Assert.True(
                unverified.Length == 0 && failed == 0 && notRun == 0 && unknown == 0 &&
                mathematicalOnly == 0 && partial == 0 && vacuous == 0 && failedArtifacts.Length == 0,
                $"Translation evidence is not a set of verified machine-int proofs " +
                $"({failed} failed, {notRun} not run, {unknown} unknown, " +
                $"{mathematicalOnly} mathematical-only, {partial} partial, {vacuous} vacuous, " +
                $"{failedArtifacts.Length} unverified artifact(s)). " +
                "A mathematical-int claim proves unbounded integers, not C# int; a partial claim " +
                "proves partial correctness. Use a bounded Z3 claim for a total machine-int proof. " +
                "Run `fmverify prove`:\n" +
                string.Join("\n", unverified.Concat(failedArtifacts)));
        }
    }
}
