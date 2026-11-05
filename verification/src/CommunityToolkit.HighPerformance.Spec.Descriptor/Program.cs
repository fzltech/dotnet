using System.Text.Json;
using CommunityToolkit.HighPerformance.Spec;
using CommunityToolkit.HighPerformance.Spec.Descriptor;

// Project map is passed relative to repo root; the skill resolves absolute paths itself.
var projectMap = new Dictionary<string, string>
{
    ["spec"] = "verification/src/CommunityToolkit.HighPerformance.Spec",
    ["descriptor"] = "verification/src/CommunityToolkit.HighPerformance.Spec.Descriptor",
    ["gate"] = "verification/test/CommunityToolkit.HighPerformance.Verification",
};

var registry = new CapabilityRegistry();

// The honesty check on its own: does every advertised class still discharge? This is what the proof
// gate script runs, and it is separate from the JSON emit below because the two answer different
// questions -- conflating them left a layer that had EARNED a ConformanceBacked property unable to
// run its own verify script. The emit refuses such a claim (correctly, see below) and the capability
// check sat behind that refusal, so the reward for bridging a property was a red gate.
//
// A backing is a fact about the layer, not a reason to stop checking whether its model still proves.
//
// This mode writes nothing, which is the other half of why verify.sh calls it rather than
// --emit-trust-boundary: a verification step that regenerates the trust boundary would silently
// repair the staleness the gate exists to catch.
if (args.Contains("--check"))
{
    // Evaluated once. ProofOfCapability runs a full obligation set against the solver, so calling
    // it twice per class doubles the check's runtime for nothing.
    var classes = registry.AllClasses()
        .Select(cls => (Class: cls, Result: cls.ProofOfCapability()))
        .ToArray();

    foreach (var (cls, result) in classes)
        Console.WriteLine(
            $"  {cls.Id}: {(result.Outcome == Outcome.Proven ? "proved" : result.Outcome.ToString().ToUpperInvariant())}");

    var unproven = classes.Where(x => x.Result.Outcome != Outcome.Proven).ToArray();
    if (unproven.Length == 0) return 0;

    foreach (var (cls, result) in unproven)
        Console.Error.WriteLine(
            $"Advertised class '{cls.Id}' did not discharge: {result.Name} was {result.Outcome}."
            + (result.Counterexample is null ? "" : $"\n{result.Counterexample}"));

    return 1;
}

// Rendering the committed document is not certifying it. TRUST-BOUNDARY.md is a derived artefact
// that the proof gate re-renders and compares on every run, and the same gate refuses any
// ConformanceBacked claim without a passing check — so an over-claim written here is caught there,
// by the only surface that can actually run the target. Refusing to render would instead make the
// document impossible to regenerate for exactly the layers that earned the stronger claim.
if (args.Contains("--emit-trust-boundary"))
{
    var path = TrustBoundary.LocateFile();
    File.WriteAllText(path, TrustBoundary.Render(registry));
    // stdout, not stderr: this mode writes no JSON, so a status line on stdout reads as a status
    // line. (The JSON emit below is the one mode where stdout is a document, and it writes none.)
    Console.WriteLine($"Wrote {path}");
    return 0;
}

// The JSON document is a different matter: it travels to consumers who cannot see this layer's gate,
// so it must carry only what this process can stand behind. Empty checks here is a statement rather
// than an omission — the descriptor does not run the target, so it is in no position to certify a
// claim about the target's behaviour, and it refuses to serialise one.
var unjustified = TrustBoundary.UnjustifiedBackings(registry, []);
if (unjustified.Count > 0)
{
    Console.Error.WriteLine(
        "Properties claim ConformanceBacked, which the descriptor cannot verify because it does not "
        + "run the target: " + string.Join(", ", unjustified)
        + "\nThe proof gate is where that claim is established. Emit the document from there, or "
        + "declare ModelOnly.");
    return 1;
}

// The proof gate asserts this too, but the document travels without the gate beside it, and an
// engine named here is a claim about how far a proof reaches.
var misattributed = TrustBoundary.MisattributedEngines(registry);
if (misattributed.Count > 0)
{
    Console.Error.WriteLine(
        "Properties name a proof engine that did not discharge them: " + string.Join(", ", misattributed)
        + "\nA PropertyDefinition is proven by the bounded Verifier, and an encoded property takes the "
        + "engine of its class.");
    return 1;
}

var doc = Descriptor.Build(registry, projectMap);

var options = new JsonSerializerOptions { WriteIndented = true };
Console.WriteLine(JsonSerializer.Serialize(doc, options));

// Fail loudly if the registry claimed a class it cannot currently prove (honesty rule).
return Descriptor.AllAdvertisedClassesProved(registry) ? 0 : 1;
