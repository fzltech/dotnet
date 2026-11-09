using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>The category of property; extends as the engine gains machinery.</summary>
public enum PropertyKind { Safety, Liveness }

/// <summary>
/// Which proof engine discharges a property. <see cref="Bounded"/> is the Z3
/// <see cref="Verifier"/>, which proves a <see cref="PropertyDefinition"/> over the model's finite
/// bound. <see cref="Unbounded"/> is Dafny, through <see cref="DafnyEngine"/>, which proves an
/// <see cref="UnboundedProperty"/> for every size — present only in a layer that opted in.
///
/// <para>The engine is a property of the capability class, not a setting on a property. A flag a
/// property could flip would promote a bounded search to an unbounded proof by assertion, and
/// <see cref="TrustBoundary.MisattributedEngines"/> rejects exactly that: an encoded property must
/// name the engine of its class, and a <see cref="PropertyDefinition"/> is always bounded.</para>
/// </summary>
public enum ProofEngine { Bounded, Unbounded }

/// <summary>
/// How far a property's proof actually reaches. Every member is optional: a property
/// genuinely proven without bounds should not have to invent one, and a required field
/// invites a placeholder, which is worse than an absent value.
///
/// <para>This is deliberately data rather than prose. <see cref="PropertyDefinition.Meaning"/>
/// says what is claimed; this says how far the claim goes, and a release gate can read it
/// without parsing English.</para>
/// </summary>
/// <param name="Bound">The finite bounds of the model this was proven over, if any — e.g. "3 containers, 4 items".</param>
/// <param name="Preconditions">Modelling assumptions stated as conditions — e.g. "a single profile per nest".</param>
/// <param name="ArithmeticDomain">
/// real | integer | bit-vector | float. Names the gap between the model's arithmetic and the
/// target's — Z3 integers are unbounded, an <c>Int32</c> is not, and an overflow lives exactly in
/// that gap.
///
/// <para><b>You can often prove the gap away instead of documenting it.</b> Asserting the target's
/// range as an invariant — the value stays within <c>[0, int.MaxValue]</c>, say — turns "the model
/// abstracts this" into a refutable property: a transition that could produce an unrepresentable
/// value then fails Consecution instead of passing quietly. That needs no bit-vector theory, only
/// the bound written down and the value in question held as state. An overflow is invisible until
/// the thing that overflows is something the model carries.</para>
/// </param>
/// <param name="Notes">What genuinely does not fit a field above.</param>
public sealed record PropertyScope(
    string? Bound = null,
    string? Preconditions = null,
    string? ArithmeticDomain = null,
    string? Notes = null);

/// <summary>
/// Whether a property constrains the target's code or only the independent model.
///
/// <para>AUTHOR OBLIGATION: <see cref="ConformanceBacked"/> may only be declared once a
/// conformance bridge exists and its check passes for this property. Until then both the
/// gate and the descriptor reject it. Defaulting to <see cref="ModelOnly"/> means a
/// property that forgets to declare its backing under-claims rather than over-claims.</para>
/// </summary>
public enum TrustBacking { ModelOnly, ConformanceBacked }

/// <summary>Why a conformance check failed. Each names a different defect, and they are not
/// interchangeable — the wrong name sends a reader to the wrong file.</summary>
public static class FailureKind
{
    /// <summary>The target took a step the model has no transition for.</summary>
    public const string ModelIncomplete = "model-incomplete";

    /// <summary>
    /// The encoding returned a transition label the model never enumerated. <c>Label</c> is an
    /// ordinary constructible type, but only the entries in <c>TransitionRelation.Labels</c> have
    /// had a consecution and a non-vacuity obligation discharged for them — one built by hand is
    /// backed by no proof at all.
    /// </summary>
    public const string LabelNotInModel = "label-not-in-model";

    /// <summary>
    /// The model has this label, but its transition cannot describe the pre/post pair the target
    /// actually produced. Distinct from <see cref="ModelIncomplete"/>: there the model is silent,
    /// here it contradicts. A transition whose footprint disagrees with its effects lands here,
    /// and no per-state check can see it because both states are individually fine.
    /// </summary>
    public const string TransitionNotModelled = "transition-not-modelled";

    /// <summary>An encoded concrete state does not satisfy the property's invariant.</summary>
    public const string InvariantViolated = "invariant-violated";

    /// <summary>
    /// A scenario kept producing steps past the limit, so the checker stopped rather than running
    /// forever.
    ///
    /// <para>The usual cause is an adapter over a target that mutates in place: it hands back the
    /// state it was given, so the state never advances and there is always another step. Without
    /// this bound that adapter does not fail — it hangs, and a CI job runs to its timeout with
    /// nothing said about why.</para>
    /// </summary>
    public const string AdapterDidNotTerminate = "adapter-did-not-terminate";

    /// <summary>
    /// The encoding of a concrete state is self-contradictory, so every check over it passes
    /// vacuously. This is the ONLY way a wrong encoding produces a false pass rather than a false
    /// failure, which makes it the most dangerous defect the bridge can have — and the cheapest
    /// to detect.
    /// </summary>
    public const string EncodingUnsatisfiable = "encoding-unsatisfiable";

    /// <summary>
    /// A method declared <c>[Deterministic]</c> produced a different trace on a second drive of the
    /// same scenario, or — on the run shape, where the checker observes a whole run rather than
    /// driving it a step at a time — different events on a second observation of it. The
    /// declaration is not taken on trust: the checker repeats the work.
    ///
    /// <para>One kind for both shapes, because the defect and what a reader does about it are the
    /// same and only what was compared differs: two replays on the step shape, the two event
    /// sequences the target itself emitted on the run shape.</para>
    /// </summary>
    public const string AnnotationNondeterministic = "annotation-nondeterministic";

    /// <summary>
    /// A <c>[Requires]</c>, <c>[Ensures]</c> or <c>[Invariant]</c> predicate returned <c>false</c> on
    /// an observed state. The code's own declared contract, violated.
    /// </summary>
    public const string AnnotationContract = "annotation-contract";

    /// <summary>
    /// A type-level <c>[Invariant]</c> or <c>[Measure]</c> whose declaring type this run handed over,
    /// which no receiver applied. The annotation was read out of the code and then checked against
    /// nothing, and unread is not satisfied.
    ///
    /// <para>Scoped to types the run actually handled, deliberately. One target assembly can hold a
    /// run-shaped seam and a step-shaped one, and an annotation belonging to the other bridge is not
    /// this run's obligation — reporting it would be a red gate an adopter could only clear by
    /// deleting an annotation that is true of their code.</para>
    ///
    /// <para>Member-level contracts have their own kinds — <see cref="AnnotationContract"/> and
    /// <see cref="AnnotationRunContract"/> — because a contract never checked carries a hypothesis
    /// this does not: that the observation's members are the wrong way round.</para>
    /// </summary>
    public const string AnnotationNoneApplied = "annotation-none-applied";

    /// <summary>
    /// A predicate held on every observed state without checking anything: every <c>all(...)</c> in
    /// it ran over an empty collection, so it is true and could not have been false.
    ///
    /// <para>A failure rather than a report, unlike the coverage numbers. An unreached annotation may
    /// belong to another bridge and is somebody else's business; a predicate that ran, on this
    /// bridge's receiver, and looked at nothing is this bridge's own vacuous green — the shape this
    /// layer refuses everywhere else it appears, and the same rule <see cref="AnnotationMeasure"/>
    /// applies when a measure never falls.</para>
    ///
    /// <para>Only when <em>every</em> evaluation across the run was vacuous. A collection that
    /// happens to be empty for one scenario is legitimate.</para>
    /// </summary>
    public const string AnnotationVacuous = "annotation-vacuous";

    /// <summary>
    /// An annotation's predicate expression does not parse. The detail names the attribute, the
    /// member it was written on, and the position in the expression.
    ///
    /// <para>Distinct from <see cref="AnnotationContract"/>, which is a predicate that ran and was
    /// false. A name that parses but resolves to nothing is not this kind either — that reuses the
    /// contract kinds, because "the code does not have what it claims" is already what they
    /// mean.</para>
    /// </summary>
    public const string AnnotationMalformed = "annotation-malformed";

    /// <summary>
    /// A <c>[Measure]</c> quantity did not decrease on a progress step, so the model's ranking
    /// function is anchored to something that does not actually fall.
    /// </summary>
    public const string AnnotationMeasure = "annotation-measure";

    /// <summary>
    /// A <c>[Step]</c> declaration names a transition the model has no label for, or the observed run
    /// produced a label no declaration covers.
    /// </summary>
    public const string AnnotationStepMismatch = "annotation-step-mismatch";

    /// <summary>
    /// The annotation scan could not read every type in the target assembly, so an annotation on
    /// one of the types it could not read would have gone unread — and unread is not satisfied.
    ///
    /// <para>A failure rather than a warning: nothing surfaces a warning on a check that otherwise
    /// passes, so a partial scan would leave the run claiming it checked what the code declares
    /// when it could not see all of it.</para>
    /// </summary>
    public const string AnnotationScanIncomplete = "annotation-scan-incomplete";

    /// <summary>
    /// The target's annotation allowance does not match the one this layer was generated with:
    /// either it declares a different version, or it carries annotations but declares no version
    /// marker at all (a copy made before the marker existed). The checker matches attribute types by
    /// full name, so an old copy that predates a renamed or reshaped attribute would let it go
    /// unread without a sound.
    ///
    /// <para>A target with no annotations at all is skipped: an unannotated target is a legitimate
    /// state, not drift. The fix is <c>fmverify add-annotations --force</c>, which the message names.</para>
    /// </summary>
    public const string AnnotationVersionMismatch = "annotation-version-mismatch";

    /// <summary>
    /// <c>Encode</c> threw while mapping a concrete state onto the model's. Distinct from
    /// <see cref="EncodingUnsatisfiable"/>: that encoding was built and contradicts itself, this one
    /// could not be built at all — the usual cause being a concrete state the model's state sort has
    /// no cell for, such as a scenario whose population exceeds the model's bound.
    ///
    /// <para>Caught and reported rather than propagated, because an exception out of the bridge
    /// takes the whole gate down with a stack trace instead of naming the state that did not fit.</para>
    /// </summary>
    public const string EncodingThrew = "encoding-threw";

    /// <summary>
    /// A <c>[Requires]</c>, <c>[Ensures]</c> or <c>[Invariant]</c> predicate returned <c>false</c>,
    /// or did not resolve, on an object a run-shaped observation handed over.
    ///
    /// <para>Distinct from <see cref="AnnotationContract"/> and deliberately so: this one says the
    /// code's contract held (or did not) across a whole run, and says nothing about any state
    /// part-way through it. Folding the two together would let the weaker claim read as the
    /// stronger one.</para>
    ///
    /// <para>Resolution failure lands here too, because it is the mistake <c>nameof</c> cannot
    /// catch: <c>[Ensures(nameof(Scheduler.InputIsWellFormed))]</c> compiles and is still wrong,
    /// since <c>[Ensures]</c> is checked on the result. <c>nameof</c> buys typo and rename safety,
    /// not receiver safety.</para>
    /// </summary>
    public const string AnnotationRunContract = "annotation-run-contract";

    /// <summary>
    /// A <c>[Measure]</c> read across a run's own event sequence established no decrease. Four
    /// causes: the quantity rose, it never fell, it was read on a single event, or it names no
    /// readable <c>int</c> member on a type the run's events carried and so went unread on every
    /// one of them.
    ///
    /// <para>Non-increasing is not enough. A constant sequence anchors no termination claim, so
    /// accepting it would make the annotation decoration — the rule is non-increasing across the
    /// sequence <b>with at least one strict decrease</b>. Strict decrease per event would be wrong
    /// in the other direction: a run emits both start and finish events and remaining work only
    /// falls on the finishes. A single reading cannot exhibit a decrease either, and an annotation
    /// nothing could read is unread rather than satisfied.</para>
    /// </summary>
    public const string AnnotationRunMeasure = "annotation-run-measure";

    /// <summary>
    /// A run-shaped observation carried no events, so it compared nothing.
    ///
    /// <para>The non-vacuity kind for this shape, and a failure rather than the warning the step
    /// shape emits for a scenario that took no steps. On this shape an empty event list means the
    /// measure was not checked at all, and a check that examined nothing must not report as a check
    /// that passed.</para>
    /// </summary>
    public const string RunObservedNothing = "run-observed-nothing";
}

/// <summary>One conformance failure, carrying enough to replay it.</summary>
/// <param name="Kind">One of <see cref="FailureKind"/>'s constants.</param>
/// <param name="Scenario">The scenario that produced it, as the adopter's own type prints it.</param>
/// <param name="Detail">What went wrong and what to do about it.</param>
public sealed record ConformanceFailure(string Kind, string Scenario, string Detail);

/// <summary>
/// How the scenarios for a run were chosen.
///
/// <para>Reported by whatever produced the scenarios and carried through the checker into the
/// report — never asserted by the registry. A registry that declared "500 sampled, seed 12" while
/// the run used something else would make the property's recorded scope a lie, and the same
/// reasoning is why <c>TRUST-BOUNDARY.md</c> is generated rather than written.</para>
///
/// <para>Three cases, not two. A sampled run is neither exhaustive nor truncated: nothing was cut
/// short and the space was not covered, so reporting it as TRUNCATED would read as a warning about
/// a run doing exactly what was asked of it.</para>
/// </summary>
public sealed record ScenarioSource
{
    // Private: the three statics/factory below are the only way to construct a ScenarioSource.
    // A public constructor with a defaulted seed would let "sampled" exist with no seed —
    // exactly the unreplayable-run-reported-as-replayable defect this type exists to prevent.
    private ScenarioSource(string kind, int? seed = null)
    {
        Kind = kind;
        Seed = seed;
    }

    /// <summary>One of <see cref="Exhaustive"/>, <see cref="Truncated"/> or a sample.</summary>
    public string Kind { get; }

    /// <summary>The seed a sampled run can be replayed with; null for the other two.</summary>
    public int? Seed { get; }

    /// <summary>The scenario space was enumerated in full.</summary>
    public static ScenarioSource Exhaustive { get; } = new("exhaustive");

    /// <summary>Enumeration hit a cap. A warning sign: the claim is narrower than it looks.</summary>
    public static ScenarioSource Truncated { get; } = new("truncated");

    /// <summary>A deliberate sample, replayable with <paramref name="seed"/>.</summary>
    public static ScenarioSource Sampled(int seed) => new("sampled", seed);

    /// <summary>What a run's coverage should say for itself.</summary>
    public string Describe() => Kind switch
    {
        "exhaustive" => "enumeration exhaustive",
        "truncated" => "enumeration TRUNCATED",
        _ => $"sampled with seed {Seed}",
    };
}

/// <summary>
/// The result of checking one property against the target's observed behaviour.
///
/// <para>These types live in Spec rather than beside the checker because the gate reaches them
/// through <c>ICapabilityRegistry</c>, and Spec must never reference the conformance project. They
/// are contract — the shape of the evidence a property's backing rests on — not checker internals.</para>
/// </summary>
/// <param name="Failures">Every failure found. Empty is necessary for a pass but not sufficient.</param>
/// <param name="Warnings">Things a human should look at that are not themselves failures.</param>
/// <param name="ScenariosChecked">How many scenarios were driven.</param>
/// <param name="StepsObserved">How many steps the target actually took across all of them.</param>
/// <summary>
/// Which of the target's annotations one run put in front of a receiver, and which it never reached.
///
/// <para>A fact about the run, in the same category as <see cref="ConformanceReport.ScenariosChecked"/>
/// — coverage is reported by the run, never asserted by the registry. <b>Nothing derives a verdict
/// from it</b>, and that is what makes <see cref="Found"/> honest as an assembly-wide number: "not
/// reached by this run" is true whoever owns the annotation, where "should have been reached" would
/// need to know whose it was.</para>
///
/// <para>An unreached annotation is not a failure. A bridge legitimately reaches only the
/// annotations on the types it hands over, so one check's <see cref="Unreached"/> is another's
/// business. What matters is the <em>union</em> across every registered check: an annotation reached
/// by none of them was read out of the code and checked by nothing, and the gate reports that
/// separately.</para>
/// </summary>
/// <param name="Found">
/// Annotations the scan read out of the target assembly: the four predicate and measure kinds, plus
/// <c>[Step]</c>. Assembly-wide, so it includes annotations belonging to a different bridge.
/// </param>
/// <param name="Applied">How many of them this run checked. For the predicate kinds that means put
/// in front of a receiver; for a <c>[Step]</c>, compared against the labels the run produced.</param>
/// <param name="Unreached">The rest, phrased as the failure detail phrases them, so a reader who
/// meets an annotation in both places recognises it as one thing.</param>
/// <param name="SeamDeclarations">
/// The <c>[Deterministic]</c> declarations the target carries, where this run drove its seam twice
/// and compared the traces — empty otherwise.
///
/// <para><b>Named rather than counted, and the distinction is the honest part.</b> Every other kind
/// is checked per declaration: a predicate is read on a receiver, a <c>[Step]</c> is compared
/// against a label. A <c>[Deterministic]</c> is not — the run drives <em>the seam</em> twice and
/// never learns which declaration that seam is, so putting these in <see cref="Applied"/> would
/// assert an attribution nobody has, and putting them in <see cref="Unreached"/> would deny a check
/// that ran. Telling one from the other needs the seam-signature cross-check that is still open.</para>
///
/// <para>It exists because the alternative was worse. The count omitted these kinds entirely while
/// its own wording said "annotations", so the first coverage report from a target annotated only
/// with <c>[Deterministic]</c> — where an adopter starts, it being the one annotation that needs no
/// predicate — read as <em>your annotations are not being read</em>.</para>
/// </param>
public sealed record AnnotationCoverage(
    int Found,
    int Applied,
    IReadOnlyList<string> Unreached,
    IReadOnlyList<string> SeamDeclarations);

/// <summary>
/// Which cells of the model's state the encoding actually pinned, and which it left free.
///
/// <para>A coverage map of the bridge's other half. <c>Encode</c> is asked to conjoin one equality
/// per state variable, and an encoding that omits one is not caught by anything else until it
/// surfaces downstream as a false <em>failure</em> — by which point it looks like a defect in the
/// target rather than a hole in the bridge.</para>
///
/// <para><b>A free cell is not a failure</b>, which is why this is a report. A slice may legitimately
/// not constrain part of the model, and nothing here can tell that from an omission. Same rule as
/// <see cref="AnnotationCoverage"/>: a fact about what the run reached, never a verdict.</para>
///
/// <para>This answers the narrower of the two questions E1 asks. It reports cells the model
/// <em>has</em> and the encoding did not determine; it cannot report a part of the target's output
/// the model has no cell for at all, which is the shape of the defect that motivated it — a
/// completion timestamp left unset, invisible because the bridge replayed the event stream and the
/// model had nowhere to hold the final record. <c>ADOPTION.md</c> asks that half as a question,
/// because no probe can find a variable nobody declared.</para>
/// </summary>
/// <param name="Constrained">Cells the encoding determined: no satisfying assignment differs in one.</param>
/// <param name="Free">
/// Cells it did not. Established by asking the solver for a second assignment that differs in the
/// cell, rather than by reading the constraint's syntax — an equality can mention a variable without
/// pinning it.
/// </param>
/// <param name="NotExamined">
/// Members of <c>StateVars</c> this probe could not interpret as a cell or a list of them. Reported
/// rather than skipped: a coverage map that quietly omits what it did not look at is worse than
/// none.
/// </param>
/// <param name="UnpinnedDraws">
/// Draws a transition reads and no step reported a value for, so the transition was checked against
/// <em>some</em> draw rather than the one the code made — a weaker comparison than it appears, and
/// the only place a reader finds that out.
///
/// <para>Reported rather than failed, deliberately. A bridge written before <c>Step.Draws</c>
/// existed reports none, and an upgrade must not turn it red for a capability it predates. A
/// property whose proof rests on a draw should not be <c>ConformanceBacked</c> while its name is in
/// here.</para>
/// </param>
/// <param name="UnreadDraws">
/// Values a step reported under a name no transition reads. Nothing pinned them, and a naming
/// mistake is the likely cause — without this a bridge looks as though it reports draws while
/// pinning none of them.
/// </param>
public sealed record EncodingCoverage(
    IReadOnlyList<string> Constrained,
    IReadOnlyList<string> Free,
    IReadOnlyList<string> NotExamined,
    IReadOnlyList<string> UnpinnedDraws,
    IReadOnlyList<string> UnreadDraws);

/// <summary>
/// Annotation coverage across a whole layer rather than one check.
///
/// <para>One check's <see cref="AnnotationCoverage.Unreached"/> read alone says almost nothing: a
/// bridge legitimately reaches only the annotations on the types it hands over, so every report
/// looks negligent in isolation. What is worth knowing is the <em>intersection</em> — an annotation
/// no check reached was read out of the code and checked by nothing.</para>
///
/// <para>Lives here, in Spec, so both gate variants call one implementation. Duplicating this in the
/// xUnit and NUnit templates would let them drift, and the two must differ only in framework syntax
/// and never in what they compute.</para>
/// </summary>
public static class LayerAnnotationCoverage
{
    /// <summary>
    /// The annotations no report reached. Intersection, not concatenation: an annotation one check
    /// missed and another applied was reached, and belongs in neither the answer nor a reader's
    /// attention.
    ///
    /// <para>Reports without coverage contribute nothing rather than counting as having reached
    /// nothing — they carry no information either way. A check that ran no scenarios is harmless
    /// here for the same reason it is harmless to an intersection: it excludes nothing, and any
    /// check that did reach an annotation removes it.</para>
    /// </summary>
    public static IReadOnlyList<string> ReachedByNoCheck(IEnumerable<ConformanceReport> reports)
    {
        var unreached = reports
            .Select(r => r.Coverage)
            .OfType<AnnotationCoverage>()
            .Select(c => c.Unreached)
            .ToList();

        if (unreached.Count == 0) return [];

        return unreached
            .Aggregate(
                unreached[0].AsEnumerable(),
                (shared, next) => shared.Intersect(next, StringComparer.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The block a gate run writes for a human to read. Rendered here so both gate variants emit the
    /// same text and it is tested once.
    ///
    /// <para>Phrased as what the run reached, never as what the adopter failed to do: "reached by no
    /// check" is a fact, where "unchecked" reads as an accusation over an annotation that may be
    /// perfectly correct and simply belong to a bridge nobody registered.</para>
    /// </summary>
    public static string Describe(IEnumerable<ConformanceReport> reports)
    {
        var all = reports.ToList();
        var coverage = all.Select(r => r.Coverage).OfType<AnnotationCoverage>().ToList();
        var found = coverage.Select(c => c.Found).DefaultIfEmpty(0).Max();
        var noCheck = ReachedByNoCheck(all);

        // "0 of 0" was the whole defect this block was rewritten for, and the zero case kept a
        // second one: with no report to count, the old sentence claimed the target declared no
        // annotations at all. That is not something this report knows -- a layer can carry four
        // source-level contracts the symbolic gate proved and still have no registered conformance
        // check. Say what was measured, then point at the section that measures the rest. Name the
        // counted kinds: [Deterministic] is reported separately as a seam, and a bare "no
        // annotation" would contradict the seam block that follows for a target carrying only that.
        var lines = new List<string>();
        if (found > 0)
        {
            lines.Add($"Annotation coverage: {found - noCheck.Count} of {found} reached by a registered check.");
        }
        else
        {
            lines.Add(coverage.Count > 0
                ? "Annotation coverage: no [Requires], [Ensures], [Invariant], [Measure] or [Step] "
                  + "annotation was reached by a registered check."
                : "Annotation coverage: no registered check reported annotation coverage.");
            lines.Add("Source-level [Requires]/[Ensures] contracts are reported under Symbolic coverage.");
        }

        if (noCheck.Count > 0)
        {
            lines.Add("Reached by no check:");
            lines.AddRange(noCheck.Select(x => "  " + x));
            lines.Add(
                "These were read out of the target and checked by nothing. Either they belong to a " +
                "seam no registered check drives, or the bridge that would reach them does not " +
                "declare the object that owns it.");
        }

        // Named beside the count rather than folded into it. A run drives its seam twice without
        // learning which declaration that seam is, so this says what happened and stops there — and
        // saying it at all is what stops a target annotated only with [Deterministic] reading its
        // own report as "nothing was found".
        var seams = all
            .Select(r => r.Coverage)
            .OfType<AnnotationCoverage>()
            .SelectMany(c => c.SeamDeclarations)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (seams.Count > 0)
        {
            lines.Add("Seams driven twice and compared:");
            lines.AddRange(seams.Select(x => "  " + x));
            lines.Add(
                "A run drives the seam its bridge names, so which of these declarations that seam " +
                "is cannot be said here — they are named rather than counted for that reason.");
        }

        // Stated rather than implied: the number is only as complete as the registry, so an
        // unregistered bridge makes it look worse than it is.
        lines.Add("Only registered checks are counted; a bridge nobody registered contributes nothing.");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Encoding coverage across a whole layer rather than one check — see <see cref="EncodingCoverage"/>.
///
/// <para>A separate class beside <see cref="LayerAnnotationCoverage"/> rather than a rename of it.
/// The two answer the same kind of question about different halves of the bridge, and folding them
/// into one type would make a reader of either wonder which half a number belonged to.</para>
/// </summary>
public static class LayerEncodingCoverage
{
    /// <summary>
    /// The cells no check pinned. Intersection, not concatenation, for the same reason
    /// <see cref="LayerAnnotationCoverage.ReachedByNoCheck"/> takes one: a cell one bridge leaves
    /// free and another constrains was constrained by the layer, and listing it would send a reader
    /// to an encoding that is already correct.
    /// </summary>
    public static IReadOnlyList<string> FreeInEveryCheck(IEnumerable<ConformanceReport> reports)
    {
        var free = reports
            .Select(r => r.Encoding)
            .OfType<EncodingCoverage>()
            .Select(e => e.Free)
            .ToList();

        if (free.Count == 0) return [];

        return free
            .Aggregate(free[0].AsEnumerable(), (shared, next) => shared.Intersect(next, StringComparer.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The block a gate run writes beside the annotation one. Silent where no check computed
    /// coverage, since a run that never encoded a state has nothing to say about encodings.
    /// </summary>
    public static string Describe(IEnumerable<ConformanceReport> reports)
    {
        var all = reports.ToList();
        var computed = all.Select(r => r.Encoding).OfType<EncodingCoverage>().ToList();
        if (computed.Count == 0) return string.Empty;

        var pinned = computed.SelectMany(e => e.Constrained)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var free = FreeInEveryCheck(all);
        var notExamined = computed.SelectMany(e => e.NotExamined)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var lines = new List<string>
        {
            $"Encoding coverage: {pinned.Count} model cell(s) pinned by an encoding, {free.Count} left free.",
        };

        if (free.Count > 0)
        {
            lines.Add("Free in every check:");
            lines.AddRange(free.Select(x => "  " + x));
            lines.Add(
                "No encoding determined these, so the solver is free to choose them and a check over " +
                "them proves less than it appears to. Usually a missing equality in Encode; " +
                "legitimately empty where the slice does not use that part of the model.");
        }

        if (notExamined.Count > 0)
        {
            lines.Add("Not examined:");
            lines.AddRange(notExamined.Select(x => "  " + x));
            lines.Add(
                "These members of StateVars are neither an integer cell nor a list of them, so " +
                "nothing above says anything about them.");
        }

        var unpinned = computed.SelectMany(e => e.UnpinnedDraws)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unread = computed.SelectMany(e => e.UnreadDraws)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (unpinned.Count > 0)
        {
            lines.Add("Draws no step reported a value for:");
            lines.AddRange(unpinned.Select(x => "  " + x));
            lines.Add(
                "A transition reads these and the bridge did not say what the code drew, so those " +
                "steps were checked against some draw rather than the one the code made. Report them " +
                "from Step.Draws. A property resting on one of these should not be " +
                "ConformanceBacked.");
        }

        if (unread.Count > 0)
        {
            lines.Add("Draws reported and never read:");
            lines.AddRange(unread.Select(x => "  " + x));
            lines.Add(
                "A step reported these and no transition asks for them, so nothing pinned them — " +
                "usually a name that does not match what the model asks for.");
        }

        // The question this cannot answer, said where the answer would be read. A cell the model
        // does not have cannot appear as a free one, and that is the shape of the defect E1 names.
        lines.Add(
            "Cells the model does not have cannot appear here. What of the target's output the " +
            "slice never reads is a question for ADOPTION.md.");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <param name="Source">How the scenarios were chosen — see <see cref="ScenarioSource"/>.</param>
/// <param name="Coverage">
/// Which annotations the run reached — see <see cref="AnnotationCoverage"/>. Null where the run did
/// not compute it, which is not the same as having found none and must not be rendered as zero.
///
/// <para>Trailing and defaulted because this record is contract: it is public in Spec and an adopter
/// may construct one, so a required positional parameter would break their source to add a number
/// nothing gates on.</para>
/// </param>
/// <param name="Encoding">
/// Which cells of the model's state the encoding pinned — see <see cref="EncodingCoverage"/>. Null
/// where the run did not compute it, and for the same reason as <paramref name="Coverage"/>: absent
/// is not the same as empty, and rendering it as "nothing constrained" would libel a working bridge.
/// </param>
public sealed record ConformanceReport(
    IReadOnlyList<ConformanceFailure> Failures,
    IReadOnlyList<string> Warnings,
    int ScenariosChecked,
    int StepsObserved,
    ScenarioSource Source,
    AnnotationCoverage? Coverage = null,
    EncodingCoverage? Encoding = null)
{
    /// <summary>
    /// Non-vacuity for the scenario set, and the reason this is not simply
    /// <c>Failures.Count == 0</c>. A run over no scenarios, or over scenarios that never step,
    /// compared nothing — and "no failures" would read as conformance. Same reasoning as the
    /// per-label non-vacuity obligations, one level out.
    /// </summary>
    public bool Passed => Failures.Count == 0 && ScenariosChecked > 0 && StepsObserved > 0;

    /// <summary>
    /// What a property's <c>Scope.Bound</c> should record about this run.
    ///
    /// <para>A backing earned over 500 sampled scenarios is a materially different claim from one
    /// earned over an exhaustive 24, and this string is where a reader of <c>TRUST-BOUNDARY.md</c>
    /// finds out which they are holding.</para>
    /// </summary>
    public string BoundDescription =>
        $"{ScenariosChecked} scenario(s), {StepsObserved} step(s), {Source.Describe()}";
}

/// <summary>
/// Evidence that one property's claim about the real code holds, and how to produce it.
///
/// <para>Declared here in Spec so <c>TrustBoundary</c> can reason about it, but never constructed
/// here: running a check means driving the target, and the target is reachable only from the
/// conformance project. Spec must not reference that project — a model that depends on the code it
/// models is not an independent model — so the checks are built there and handed to the gate.</para>
/// </summary>
/// <param name="PropertyId">The property this evidence is for.</param>
/// <param name="Run">
/// AUTHOR OBLIGATION: this delegate MUST drive the real target through
/// <c>ConformanceChecker.Check</c> and return its actual report. A canned passing report would
/// silently turn the strongest claim this layer can make into a lie, and nothing downstream can
/// tell the difference.
/// </param>
public sealed record ConformanceCheck(string PropertyId, Func<ConformanceReport> Run);

/// <summary>
/// Where the conformance project hands its checks to the rest of the layer.
///
/// <para>The seam exists because of a layering fact: the gate's earned-backing assertion is
/// generated by <c>init</c>, before any bridge exists, while the checks can only be built in the
/// conformance project, which <c>add-bridge</c> adds later and which Spec must never reference. Two
/// assertions of one rule would not work — the one without evidence always rejects — so there is one
/// assertion and the evidence is registered into it.</para>
///
/// <para><b>Empty is the safe state, and that is deliberate.</b> If registration never happens —
/// no bridge, a bridge whose registration was removed, a consumer that only references Spec — the
/// list stays empty and every <c>ConformanceBacked</c> claim is rejected. The failure mode of this
/// seam is over-strictness, never a claim let through unchecked.</para>
/// </summary>
public static class ConformanceEvidence
{
    private static IReadOnlyList<ConformanceCheck> _checks = [];

    /// <summary>
    /// Called once by the conformance gate as its assembly loads, before any assertion runs.
    /// Registering a check is not the same as passing it: each is still run and must pass.
    /// </summary>
    public static void Register(IReadOnlyList<ConformanceCheck> checks) => _checks = checks;

    public static IReadOnlyList<ConformanceCheck> Checks => _checks;
}

/// <summary>
/// A class of property the engine can discharge, e.g. "safety proven by inductive
/// invariant". <see cref="ProofOfCapability"/> is a live example VC of this class;
/// the descriptor emitter runs it and refuses to advertise a class that does not prove.
/// </summary>
public sealed record PropertyClass(
    string Id,
    PropertyKind Kind,
    string Meaning,
    string ObligationShape,
    string TemplateId,
    /// <summary>
    /// AUTHOR OBLIGATION: this delegate MUST invoke the real verification engine
    /// (e.g. run the full obligation/VC set for this class against Z3) and return
    /// its actual outcome — never a canned/hardcoded <see cref="Outcome.Proven"/>.
    /// The descriptor emitter (<c>Descriptor.Build</c>) and the honesty checks
    /// (<c>Descriptor.AllAdvertisedClassesProved</c>) trust this delegate's
    /// <see cref="VcResult.Outcome"/> as the sole gate on whether a capability is
    /// advertised. A stubbed "always Proven" implementation would silently defeat
    /// honesty-by-construction (I-HONEST-CAPS, spec §6.3) by advertising a capability
    /// the engine cannot actually discharge.
    /// </summary>
    Func<VcResult> ProofOfCapability,

    /// <summary>The engine <see cref="ProofOfCapability"/> runs, and so the engine every property
    /// of this class is discharged by.</summary>
    ProofEngine Engine = ProofEngine.Bounded);

/// <summary>
/// The descriptor (skill-facing) view of an already-encoded property, for dedup.
/// <see cref="Engine"/> must match the engine of the class <see cref="ClassId"/> names, and is
/// <see cref="ProofEngine.Bounded"/> for anything projected from a <see cref="PropertyDefinition"/>.
/// </summary>
public sealed record EncodedProperty(
    string Id,
    PropertyKind Kind,
    string Meaning,
    string ClassId,
    PropertyScope? Scope = null,
    TrustBacking Backing = TrustBacking.ModelOnly,
    ProofEngine Engine = ProofEngine.Bounded);

/// <summary>
/// A self-contained, runnable property: an inductive invariant and the safety
/// predicate it implies. The Verifier discharges the four VCs against these builders.
///
/// <para>Always <see cref="ProofEngine.Bounded"/>, which is why there is no engine member here:
/// the builders below are Z3 expressions over the model's finite bound, and the
/// <see cref="Verifier"/> is the only thing that discharges them.</para>
/// </summary>
public sealed record PropertyDefinition(
    string Id,
    PropertyKind Kind,
    string ClassId,
    string Meaning,
    Func<Formulas, StateVars, BoolExpr> Invariant,
    Func<Formulas, StateVars, BoolExpr> Safety,
    PropertyScope? Scope = null,
    TrustBacking Backing = TrustBacking.ModelOnly,

    /// <summary>
    /// A ranking function: an <b>integer</b>-valued expression over the state, bounded below by the
    /// invariant and strictly decreasing on every progress transition.
    ///
    /// <para>The integer requirement is not a style choice. A real-valued measure can decrease
    /// forever — 1, 1/2, 1/4, … — without ever crossing a bound, so it is not a well-founded
    /// argument and proves nothing about termination. The Verifier checks the sort and refuses a
    /// real.</para>
    ///
    /// <para><b>A good measure counts the work not yet done</b> — items still unplaced, iterations
    /// still to run. The recurring wrong answer names the value being <i>optimised</i> instead,
    /// which fails Decrease for a reason that reads like a bug in the tool.</para>
    ///
    /// <para><b>The loop with a timeout.</b> This is the case most real code presents, and it can be
    /// stated two ways that sound alike and are not.</para>
    ///
    /// <para>The timeout exit is not covered by any measure over the loop's own state, so the honest
    /// way to encode it is a nondeterministic abort transition that may fire in any state. The
    /// theorem is then <i>"the loop terminates, either by exhausting its bound or by aborting"</i> —
    /// which is true, and worth far less than it sounds, because <b>the abort is what does the
    /// work</b>. A loop that could otherwise spin forever satisfies it.</para>
    ///
    /// <para>The claim worth having is the other one: <i>every bounded loop terminates on its own
    /// measure, independently of any timeout</i>. That makes the timeout a <b>safety net</b> rather
    /// than the thing that halts the system, and it means a later change that breaks a loop's
    /// measure is caught by the gate instead of absorbed by the timeout. Prove that one, and leave
    /// the timeout out of the model.</para>
    ///
    /// <para><b>The proof covers the transitions you modelled, and no others.</b> A termination
    /// result says the modelled transition system has no infinite run. A step the real system takes
    /// that your model has no transition for is simply not in the theorem, and nothing reports the
    /// omission — the claim stays true and becomes far narrower than it reads. The case that bites
    /// is a loop iteration that makes no progress: if every transition is "something moved", the
    /// measure is unchanged by an iteration where nothing moved, so you prove "only finitely many
    /// things move" while the loop may spin for ever between them. Before believing a termination
    /// result, ask what the real loop does on an iteration where the measure does not change. If it
    /// iterates anyway, that transition belongs here — and the measure must decrease on it too,
    /// which it can only do if something bounds it.</para>
    ///
    /// <para><b>A termination claim is always <see cref="TrustBacking.ModelOnly"/>.</b> The
    /// conformance checker compares encoded states against the invariant and observed steps against
    /// the transition relation; it knows nothing about measures, so no conformance check can back
    /// this claim and the gate rejects <see cref="TrustBacking.ConformanceBacked"/> on it.</para>
    ///
    /// <para>Required for <see cref="PropertyKind.Liveness"/>, forbidden otherwise.</para>
    /// </summary>
    Func<Formulas, StateVars, ArithExpr>? Measure = null,

    /// <summary>
    /// Which transitions must make progress. Progress labels must strictly decrease the measure;
    /// every other label must not increase it.
    ///
    /// <para><b><c>_ =&gt; true</c> disables an obligation.</b> It looks like the strongest setting and
    /// is often correct — if every transition really does decrease the measure, every label gets a
    /// Decrease obligation and none gets NonIncrease. But then NonIncrease is never exercised, so
    /// the safeguard against a non-progress step <i>raising</i> the measure is inert for this model.
    /// That is fine until the label set grows: add a clock tick, an idle iteration or a retry, and
    /// <c>_ =&gt; true</c> demands it decrease, producing a Decrease failure that reads like a broken
    /// measure when the predicate is what is now wrong. Narrow it to the labels that genuinely make
    /// progress and let the rest carry NonIncrease. <c>Validate</c> rejects the opposite mistake — no
    /// label making progress, which would hold vacuously — but cannot reject this one, because
    /// "everything makes progress" is a legitimate model.</para>
    ///
    /// <para>A predicate rather than a set, because labels are parameterised over the model's bound
    /// and there can be dozens of them.</para>
    ///
    /// <para>Required for <see cref="PropertyKind.Liveness"/>, forbidden otherwise. A property for
    /// which no label makes progress is refused: every label would be checked for non-increase,
    /// nothing would decrease, and termination would hold vacuously.</para>
    /// </summary>
    Func<Label, bool>? IsProgressLabel = null);

/// <summary>
/// A claim the unbounded engine proves for every size: one member of a Dafny source, verified on
/// its own by <see cref="DafnyEngine"/>.
///
/// <para><b>It is a claim about the Dafny source, not about the target and not about the Z3
/// model.</b> The source is a second hand-written model, and nothing checks that it agrees with
/// either of the other two — so <see cref="Meaning"/> must describe what the Dafny member states,
/// and the claim is always <see cref="TrustBacking.ModelOnly"/>. There is no backing member to set,
/// and no engine member either: both are fixed by the type.</para>
/// </summary>
/// <param name="DafnyFile">The source, relative to the layer root — e.g. <c>unbounded/Seed.dfy</c>.</param>
/// <param name="Member">
/// The method, lemma or function whose specification is the claim. Verified alone, and required to
/// be declared in <paramref name="DafnyFile"/>, so a claim cannot pass by verifying something else.
/// </param>
public sealed record UnboundedProperty(
    string Id,
    PropertyKind Kind,
    string ClassId,
    string Meaning,
    string DafnyFile,
    string Member,
    PropertyScope? Scope = null)
{
    public EncodedProperty Encoded =>
        new(Id, Kind, Meaning, ClassId, Scope, TrustBacking.ModelOnly, ProofEngine.Unbounded);
}

/// <summary>
/// The capability registry and <see cref="UnboundedRegistry"/> read as one. Every surface that
/// reports on the layer — the gate, the descriptor, the trust boundary — goes through these, so a
/// claim cannot be counted by one of them and missed by another.
/// </summary>
public static class Engines
{
    public static IReadOnlyList<PropertyClass> AllClasses(this ICapabilityRegistry registry) =>
        registry.SupportedClasses.Concat(UnboundedRegistry.Classes).ToArray();

    public static IReadOnlyList<EncodedProperty> AllEncoded(this ICapabilityRegistry registry) =>
        registry.AlreadyEncoded.Concat(UnboundedRegistry.Properties.Select(p => p.Encoded)).ToArray();
}
