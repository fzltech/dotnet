using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Z3;
using CommunityToolkit.HighPerformance.Spec;

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// Drives the target through each scenario and checks, at every observed step, that the model
/// describes what the target did and that the states it passed through satisfy the property.
///
/// <para>fmverify supplies this whole — there is nothing to fill in here. The two things it depends
/// on, <see cref="IStepper{TScenario,TState}"/> and <see cref="IConcreteEncoding{TState}"/>, are
/// yours.</para>
///
/// <para><b>Model incompleteness is the failure that matters most and is the easiest to
/// overlook.</b> A step no transition describes means the target can do something the model does
/// not, so every proof over that model is narrower than it appears — and nothing else in this layer
/// will ever report it. It is not a warning.</para>
///
/// <para><paramref name="source"/> has no default on purpose. It used to be a bool defaulting to
/// "exhaustive", so a caller that sampled and said nothing was recorded as having covered the
/// space. The call site is where the answer is known, so the call site is where it is asked.</para>
/// </summary>
public static class ConformanceChecker
{
    /// <summary>
    /// One annotation argument, the type that declared it, and whether that type is the subject the
    /// annotation talks about.
    ///
    /// <para>The owner is not decoration. An <c>[Invariant]</c> or a <c>[Measure]</c> is a claim the
    /// declaring type makes about its own instances, so a <c>[Measure]</c> on an event type says
    /// nothing about a state type and must not be read against one. The scan used to keep names
    /// only, which made two annotations of the same kind on different types indistinguishable — and
    /// reported the resulting failed lookup as the adopter's annotation being wrong.</para>
    ///
    /// <para><see cref="OwnerIsSubject"/> says which of two things the owner is. For a type-level
    /// annotation it is the subject: the instances the claim is about. For a <c>[Requires]</c> or
    /// <c>[Ensures]</c>, which sits on a seam method, it is only the class that declares that seam —
    /// the object the predicate is about is whichever one the shape supplies, and on the run shape
    /// the <c>[Ensures]</c> is read on the result. Treating the owner as the subject there compared
    /// it against the result, which is not the declaring class, and skipped the annotation in
    /// silence: the decoration this layer forbids outright.</para>
    ///
    /// <para><see cref="DeclaredOn"/> is the member the annotation was written on, or the type's own
    /// name for a type-level one. It exists so that a failure about an annotation that went unread
    /// can send a reader to the line that declares it, which the predicate's name alone does not do
    /// once a class carries more than one annotated seam.</para>
    /// </summary>
    private sealed record Declared(string Name, Type Owner, bool OwnerIsSubject, string DeclaredOn);

    /// <summary>
    /// What the target's code declared, read from the live assembly.
    ///
    /// <para><see cref="LoaderErrors"/> is what the runtime said about types it could not load. It
    /// travels with the model rather than being thrown, because the types that did load are still
    /// worth checking and because a partial scan is a fact about the run that every check needs to
    /// report, not just the one that happened to populate the cache.</para>
    /// </summary>
    private sealed record AnnotationModel(
        IReadOnlyList<Declared> StepMembers,
        IReadOnlyList<Declared> DeterministicMembers,
        IReadOnlyList<Declared> RequiresMethods,
        IReadOnlyList<Declared> EnsuresMethods,
        IReadOnlyList<Declared> InvariantMethods,
        IReadOnlyList<Declared> MeasureMembers,
        IReadOnlyList<string> LoaderErrors,
        string? AllowanceVersion);

    /// <summary>
    /// One scan per assembly, kept for the rest of the process.
    ///
    /// <para>The scan walks every member of every type, which on a large target assembly is
    /// hundreds of milliseconds — and <see cref="Check"/> would otherwise pay it again for every
    /// registered check. An assembly's metadata cannot change inside a process, so the answer is
    /// the same every time it is asked. Concurrent because the gate's runner may run checks in
    /// parallel.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<Assembly, AnnotationModel> AnnotationsByAssembly = new();

    private static AnnotationModel ReadAnnotations(Assembly assembly) =>
        AnnotationsByAssembly.GetOrAdd(assembly, ScanAnnotations);

    /// <summary>
    /// One parse per distinct expression, kept for the rest of the process, with the parse error
    /// stored beside it so a malformed expression is reported without being re-parsed once per
    /// observed state.
    ///
    /// <para>Keyed by the expression text alone. A parse holds no types — an identifier's meaning is
    /// not decided until it is read off a receiver — so the same parsed expression serves a step
    /// shape's state on one bridge and a run shape's driver, result or event on another. A compiled
    /// delegate would have needed the receiver type in this key.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, (PredicateExpression? Expression, string? Error)>
        ParsedExpressions = new();

    private static (PredicateExpression? Expression, string? Error) ReadExpression(string text) =>
        ParsedExpressions.GetOrAdd(text, static t =>
            PredicateExpression.TryParse(t, out var expression, out var error)
                ? (expression, null)
                : (null, error));

    /// <summary>
    /// Reads the Fmverify.Annotations attributes from the assembly <paramref name="assembly"/> by full
    /// type name.
    ///
    /// <para>By name, deliberately: the conformance project references the target but not the
    /// annotations package, so the checker cannot name the attribute types at compile time. Matching
    /// <c>AttributeType.FullName</c> reads constructor arguments through
    /// <c>CustomAttributeData</c>, which needs no reference to the declaring type.</para>
    /// </summary>
    private static AnnotationModel ScanAnnotations(Assembly assembly)
    {
        var steps = new List<Declared>();
        var requires = new List<Declared>();
        var ensures = new List<Declared>();
        var invariants = new List<Declared>();
        var measures = new List<Declared>();
        var deterministic = new List<Declared>();
        var (types, loaderErrors) = LoadableTypes(assembly);

        // The assembly or assemblies that actually declare the allowance attribute types. A source
        // copy compiles them into the target; a referenced package keeps them in their own assembly
        // — and <c>Allowance</c> lives in whichever one that is, so the version has to be read from
        // here rather than from the scanned assembly alone.
        var allowanceAssemblies = new HashSet<Assembly>();

        foreach (var type in types)
        {
            foreach (var attr in CustomAttributeData.GetCustomAttributes(type))
            {
                CollectAllowanceAssembly(attr, allowanceAssemblies);
                Match(attr.AttributeType.FullName, attr, "Fmverify.Annotations.InvariantAttribute", invariants, type, ownerIsSubject: true, declaredOn: type.Name);
                Match(attr.AttributeType.FullName, attr, "Fmverify.Annotations.MeasureAttribute", measures, type, ownerIsSubject: true, declaredOn: type.Name);
            }

            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static))
            {
                // The declaring type, not the type being scanned. This loop enumerates inherited
                // members too, so a [Requires] on a base method is seen once for the base and once
                // for every subclass; recording the scanning type turned each of those sightings
                // into an obligation of its own, and the one belonging to a subclass the run never
                // drives was an obligation nothing could ever satisfy. Its failure even named a
                // member that subclass does not declare.
                //
                // DeclaredOnly on the scan above would also collapse them, by never looking at an
                // inherited member — but it would stop finding an annotation on a base class in
                // another assembly, which is not among the types this scan walks. Unread is not
                // satisfied, so the sighting is kept and the owner corrected instead.
                var declaring = member.DeclaringType ?? type;

                foreach (var attr in CustomAttributeData.GetCustomAttributes(member))
                {
                    CollectAllowanceAssembly(attr, allowanceAssemblies);

                    // A [Step] names a transition the seam produces, so it is located exactly like a
                    // [Requires]: the declaring type owns the seam and is not the subject of a
                    // predicate. It was a bare string until G11, which is why nothing could ask
                    // whose bridge a [Step] belonged to.
                    Match(attr.AttributeType.FullName, attr, "Fmverify.Annotations.StepAttribute", steps, declaring, ownerIsSubject: false, declaredOn: member.Name);

                    // A [Requires] or [Ensures] is on the seam method, so the declaring type is the
                    // class that owns the seam and not the object the predicate is about. A
                    // [Measure] on a member is still the declaring type's claim about its own
                    // instances, exactly as a type-level one is.
                    Match(attr.AttributeType.FullName, attr, "Fmverify.Annotations.RequiresAttribute", requires, declaring, ownerIsSubject: false, declaredOn: member.Name);
                    Match(attr.AttributeType.FullName, attr, "Fmverify.Annotations.EnsuresAttribute", ensures, declaring, ownerIsSubject: false, declaredOn: member.Name);
                    Match(attr.AttributeType.FullName, attr, "Fmverify.Annotations.MeasureAttribute", measures, declaring, ownerIsSubject: true, declaredOn: member.Name);
                    // Not through Match: [Deterministic] takes no constructor argument, so it
                    // has no name to record. The empty Name is why it is reported by owner and
                    // member rather than by predicate, and why it is named in the coverage summary
                    // rather than counted in it.
                    if (attr.AttributeType.FullName == "Fmverify.Annotations.DeterministicAttribute")
                        deterministic.Add(new Declared(string.Empty, declaring, OwnerIsSubject: false, DeclaredOn: member.Name));
                }
            }
        }

        // Distinct because an inherited annotated member is sighted once per type in the hierarchy
        // and, with the declaring type as the owner, every sighting is the same declaration. Left
        // as they are, one predicate would be invoked once per subclass on every receiver and a
        // failing one would be reported that many times.
        return new AnnotationModel(
            [.. steps.Distinct()],
            [.. deterministic.Distinct()],
            [.. requires.Distinct()],
            [.. ensures.Distinct()],
            [.. invariants.Distinct()],
            [.. measures.Distinct()],
            loaderErrors,
            ReadAllowanceVersion(allowanceAssemblies));
    }

    /// <summary>
    /// Records the assembly an allowance attribute is declared in, so <see cref="ReadAllowanceVersion"/>
    /// can find <c>Allowance</c> wherever it lives — the scanned assembly for a source copy, the
    /// referenced allowance assembly for a package.
    /// </summary>
    private static void CollectAllowanceAssembly(CustomAttributeData attr, HashSet<Assembly> into)
    {
        if (attr.AttributeType.FullName?.StartsWith("Fmverify.Annotations.", StringComparison.Ordinal) == true)
            into.Add(attr.AttributeType.Assembly);
    }

    /// <summary>
    /// The version of the target's annotation allowance, or null where it declares none — either
    /// because the target carries no allowance at all or because it was copied before the marker
    /// existed. By full type name for the same reason the attribute scan is: the conformance project
    /// does not reference the allowance, so it cannot name its types at compile time.
    /// </summary>
    private static string? ReadAllowanceVersion(IEnumerable<Assembly> allowanceAssemblies)
    {
        foreach (var assembly in allowanceAssemblies)
        {
            try
            {
                var type = assembly.GetType("Fmverify.Annotations.Allowance", throwOnError: false);
                var field = type?.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                var value = field?.GetRawConstantValue() as string;
                if (value is not null) return value;
            }
            catch
            {
                // A version marker that cannot be read is not a reason to fail the run — the scan's own
                // LoaderErrors already reports an assembly that will not load. Absent is the honest
                // answer, and it means the version check does not fire.
                continue;
            }
        }

        return null;
    }

    /// <summary>
    /// The types the runtime could load, and what it said about the ones it could not.
    ///
    /// <para><c>Assembly.GetTypes()</c> throws if a single type fails to load, and one missing
    /// optional dependency is enough. Unhandled, that takes the whole conformance check down with a
    /// loader stack trace instead of a named failure — and on a large target, an assembly that does
    /// not load in full is the ordinary case rather than the exotic one.</para>
    ///
    /// <para>The types that did load are returned and still checked. The loader's own messages come
    /// back with them, because a scan that could not see the whole assembly cannot claim it checked
    /// what the code declares: an annotation on a type that never loaded would go unread, and
    /// unread is not the same as satisfied.</para>
    /// </summary>
    private static (IReadOnlyList<Type> Types, IReadOnlyList<string> LoaderErrors) LoadableTypes(
        Assembly assembly)
    {
        try
        {
            return (assembly.GetTypes(), []);
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            var errors = ex.LoaderExceptions
                .Where(e => e is not null)
                .Select(e => e!.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // A ReflectionTypeLoadException with nothing to say would leave the failure below with
            // no cause in it, and "some types did not load" sends a reader nowhere.
            return (loaded, errors.Length > 0 ? errors : [ex.Message]);
        }
    }

    private static void Match(
        string? fullName, CustomAttributeData attr, string expected, List<Declared> sink, Type owner,
        bool ownerIsSubject, string declaredOn)
    {
        if (fullName == expected && attr.ConstructorArguments.Count > 0)
            sink.Add(new Declared(
                (string)attr.ConstructorArguments[0].Value!, owner, ownerIsSubject, declaredOn));
    }

    /// <summary>
    /// The model's state cells, walked off <c>StateVars</c> by reflection.
    ///
    /// <para><b>Reflection rather than a generated method, for two reasons.</b> <c>StateVars</c> is
    /// kind-specific — one integer for the scalar kind, a grid and a pool for the multiset one — so a
    /// method that enumerated it would have to live in <c>SmtContext</c>, which the adopter grows;
    /// a kit addition does not reach an edited copy, so this file would stop compiling for exactly
    /// the adopters whose model is worth measuring. And it finds the cells an adopter <em>added</em>,
    /// which is the case this probe exists for.</para>
    ///
    /// <para>A member that is neither an <c>IntExpr</c> nor a list of them yields a null cell and is
    /// reported as unexamined. Silently skipping it would make the coverage map claim more than it
    /// looked at.</para>
    /// </summary>
    private static IEnumerable<(string Name, IntExpr? Cell)> Cells(object state)
    {
        foreach (var property in state.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(state);

            switch (value)
            {
                case IntExpr cell:
                    yield return (property.Name, cell);
                    break;

                case System.Collections.IEnumerable rows:
                    var outer = 0;
                    var understood = false;
                    foreach (var row in rows)
                    {
                        if (row is IntExpr cell)
                        {
                            understood = true;
                            yield return ($"{property.Name}[{outer}]", cell);
                        }
                        else if (row is System.Collections.IEnumerable inner)
                        {
                            var column = 0;
                            foreach (var item in inner)
                            {
                                if (item is not IntExpr nested) continue;

                                understood = true;
                                yield return ($"{property.Name}[{outer}][{column}]", nested);
                                column++;
                            }
                        }

                        outer++;
                    }

                    // An empty list is not a member this probe failed to understand, so only a
                    // non-empty one that yielded nothing counts as unexamined.
                    if (!understood && outer > 0) yield return (property.Name, null);
                    break;

                default:
                    yield return (property.Name, null);
                    break;
            }
        }
    }

    /// <summary>
    /// Which of those cells the encoding determined, asked of the solver rather than of the
    /// constraint's syntax: an equality can mention a variable without pinning it.
    ///
    /// <para>A cell is free when a second satisfying assignment differs in it. The caller has
    /// already established that the encoding is satisfiable — that is the vacuity guard — and
    /// hands in its model, so this costs one solver call per cell.</para>
    /// </summary>
    private static EncodingCoverage ProbeEncoding(
        SmtContext context, object symbolic, BoolExpr encoded, Model witness)
    {
        var constrained = new List<string>();
        var free = new List<string>();
        var notExamined = new List<string>();

        foreach (var (name, cell) in Cells(symbolic))
        {
            if (cell is null)
            {
                notExamined.Add(name);
                continue;
            }

            var elsewhere = context.Z3.MkSolver();
            elsewhere.Assert(encoded);
            elsewhere.Assert(context.Z3.MkNot(context.Z3.MkEq(cell, witness.Eval(cell, true))));

            // UNKNOWN counts as free rather than constrained. The whole doctrine here is that an
            // unanswered question is not a proof, and over-reporting a cell as unpinned costs an
            // adopter a look at their encoding where the other way costs them the finding.
            if (elsewhere.Check() == Status.UNSATISFIABLE) constrained.Add(name);
            else free.Add(name);
        }

        return new EncodingCoverage(constrained, free, notExamined, [], []);
    }

    /// <summary>
    /// Every <c>[Step]</c> this run was ever going to check must name a transition it actually
    /// produced — the first <c>:</c>-segment of some observed step label. Kind-agnostic: the model's
    /// own label completeness is already covered by the label-not-in-model and model-incomplete
    /// checks.
    ///
    /// <para><b>Scoped to the types the run handed over</b>, using the same predicate
    /// <c>ReportUncheckedAnnotations</c> uses. Without it every <c>[Step]</c> in the annotation
    /// assembly had to match a label <em>this</em> run produced, so two bridges over different
    /// slices reddened each other — a gate an adopter could only clear by deleting an annotation
    /// that is true of their code. That over-report was fixed for contracts and left standing here,
    /// because a <c>[Step]</c> was a bare string with no owner to filter on.</para>
    ///
    /// <para>It trades a check for a report, deliberately: a bridge that hands over nothing stops
    /// checking its <c>[Step]</c>s, and they appear in the coverage summary instead. A false red is
    /// worse than a visible miss, and telling this run's <c>[Step]</c> from another bridge's needs
    /// the seam-signature cross-check that is still open.</para>
    /// </summary>
    private static IReadOnlyList<ConformanceFailure> CheckStepDeclarations(
        IReadOnlyList<Declared> checkable, IReadOnlyCollection<string> observedLabels)
    {
        return checkable
            .Where(declared => !observedLabels.Any(l => l.Split(':')[0] == declared.Name))
            .Select(declared => new ConformanceFailure(
                FailureKind.AnnotationStepMismatch, "<declaration>",
                $"a [Step] on {declared.Owner.Name}.{declared.DeclaredOn} declares transition " +
                $"'{declared.Name}', which no observed step produced " +
                $"({string.Join(", ", observedLabels)}). Fix the annotation or the slice."))
            .ToArray();
    }

    /// <summary>
    /// A <c>[Deterministic]</c> seam is driven twice on the same scenario and must produce the same
    /// step-label sequence. The declaration is re-verified on every gate run rather than trusted.
    /// </summary>
    private static IReadOnlyList<ConformanceFailure> CheckDeterminism<TScenario, TState>(
        IStepper<TScenario, TState> stepper, TScenario scenario, int maxSteps)
    {
        var first = Trace(stepper, scenario, maxSteps);
        var second = Trace(stepper, scenario, maxSteps);
        if (first.SequenceEqual(second))
            return [];

        return
        [
            new ConformanceFailure(
                FailureKind.AnnotationNondeterministic, scenario?.ToString() ?? "<scenario>",
                $"the [Deterministic] seam produced different traces: " +
                $"[{string.Join(", ", first)}] vs [{string.Join(", ", second)}]."),
        ];
    }

    private static IReadOnlyList<string> Trace<TScenario, TState>(
        IStepper<TScenario, TState> stepper, TScenario scenario, int maxSteps)
    {
        var labels = new List<string>();
        var state = stepper.Start(scenario);
        for (var i = 0; i < maxSteps && stepper.Advance(state) is { } step; i++)
        {
            labels.Add(step.Label);
            state = step.Post;
        }
        return labels;
    }

    /// <summary>
    /// A <c>[Deterministic]</c> run-shaped seam is observed twice and must emit the same events.
    ///
    /// <para>Stronger than the step shape's check, which compares two replays and can therefore
    /// agree while the target itself does not. Both sequences here are the target's own.</para>
    /// </summary>
    private static IReadOnlyList<ConformanceFailure> CheckRunDeterminism<TScenario, TDriver>(
        IRunObserver<TScenario, TDriver> observer, TScenario scenario)
    {
        // Both runs made here, each from its own Configure. Two observations of one driver are two
        // runs of the same object, which is not what [Deterministic] claims — and on a driver that
        // carries state the second would start where the first finished. It also keeps the driver
        // the scenario loop checks [Requires] on out of this: that one must reach the loop unrun.
        var first = observer.Observe(scenario, observer.Configure(scenario));
        var second = observer.Observe(scenario, observer.Configure(scenario));
        if (first.Events.SequenceEqual(second.Events))
            return [];

        return
        [
            new ConformanceFailure(
                FailureKind.AnnotationNondeterministic, scenario?.ToString() ?? "<scenario>",
                "the [Deterministic] seam emitted different events on a second run:\n" +
                $"  first:  [{string.Join(", ", first.Events)}]\n" +
                $"  second: [{string.Join(", ", second.Events)}]\n" +
                "If those two lines read the same, the event type is not value-equal: comparison " +
                "fell back to reference equality, which no honest second run can satisfy. Make the " +
                "event a record, or override Equals."),
        ];
    }

    /// <summary>
    /// Drives the target one step at a time and checks each observed step and state against the
    /// model, plus the target's own declared annotations on the states it stepped through.
    ///
    /// <para><paramref name="annotationSource"/> locates the assembly to read annotations from. It
    /// defaults to the state type's assembly, which is right when the Stepper reuses a target type
    /// as its state. A Stepper that defines its own replay state must pass a target type instead, or
    /// annotations on the target code would be invisible.</para>
    ///
    /// <para>If your target runs to completion rather than stepping — one call in, a result and a
    /// list of what happened out — the annotations on your code have nothing here to be checked
    /// against. Use <see cref="CheckRun"/> and hand it an <c>IRunObserver</c>.</para>
    /// </summary>
    public static ConformanceReport Check<TScenario, TState>(
        IStepper<TScenario, TState> stepper,
        IConcreteEncoding<TState> encoding,
        IEnumerable<TScenario> scenarios,
        PropertyDefinition property,
        ScenarioSource source,
        int maxStepsPerScenario = 1000,
        Type? annotationSource = null)
        where TState : notnull =>
        CheckCore<TScenario, TState, object>(stepper, encoding, scenarios, property, source,
            observer: null, maxStepsPerScenario, annotationSource);

    /// <summary>
    /// The same model comparison as <see cref="Check"/>, plus the target's own annotations checked on
    /// the objects one run of the target produced.
    ///
    /// <para>The model is still compared against the replay <paramref name="stepper"/> produces —
    /// that half is identical. What changes is where the code's annotations are read: <c>[Requires]</c>
    /// on the driver the run was performed with, <c>[Ensures]</c> and <c>[Invariant]</c> on the result,
    /// <c>[Measure]</c> across the events the run emitted. Every one of those is an object the code
    /// itself made, which is what makes the claim honest on a shape where the replay state is the
    /// bridge's reconstruction rather than the target's.</para>
    ///
    /// <para>Calling this rather than <see cref="Check"/> is how the shape is declared. It is not
    /// inferred: nothing the tool can read off a target tells it which shape the target has.</para>
    ///
    /// <para><paramref name="annotationSource"/> defaults to the assembly of the first observation's
    /// <c>Driver</c>, which is a target object by construction. Pass a type to override it.</para>
    /// </summary>
    public static ConformanceReport CheckRun<TScenario, TState, TDriver>(
        IStepper<TScenario, TState> stepper,
        IConcreteEncoding<TState> encoding,
        IEnumerable<TScenario> scenarios,
        PropertyDefinition property,
        ScenarioSource source,
        IRunObserver<TScenario, TDriver> observer,
        int maxStepsPerScenario = 1000,
        Type? annotationSource = null)
        where TState : notnull =>
        CheckCore(stepper, encoding, scenarios, property, source,
            observer, maxStepsPerScenario, annotationSource);

    private static ConformanceReport CheckCore<TScenario, TState, TDriver>(
        IStepper<TScenario, TState> stepper,
        IConcreteEncoding<TState> encoding,
        IEnumerable<TScenario> scenarios,
        PropertyDefinition property,
        ScenarioSource source,
        IRunObserver<TScenario, TDriver>? observer,
        int maxStepsPerScenario,
        Type? annotationSource)
        where TState : notnull
    {
        var failures = new List<ConformanceFailure>();
        var warnings = new List<string>();
        var scenariosChecked = 0;
        var stepsObserved = 0;
        var scenarioList = scenarios as IReadOnlyList<TScenario> ?? scenarios.ToList();

        // On the run shape the replay state is the bridge's own reconstruction, so typeof(TState) is
        // exactly the wrong place to look for the target's annotations — that conflation is what this
        // shape exists to undo. The first driver is a target object by construction, so it names the
        // right assembly without the adopter having to. Configuring it runs nothing, which is why
        // this can happen before anything else.
        TDriver? firstDriver = default;
        if (observer is not null && scenarioList.Count > 0)
            firstDriver = observer.Configure(scenarioList[0]);

        var annotationAssembly = annotationSource?.Assembly
            ?? (firstDriver is not null
                ? firstDriver.GetType().Assembly
                : typeof(TState).Assembly);
        var annotations = ReadAnnotations(annotationAssembly);
        var observedLabels = new HashSet<string>();

        // Every member-level contract this run actually applied to an object. A declaration missing
        // from here at the end was read out of the code and then checked against nothing, which is
        // the silence "unread is not satisfied" forbids.
        //
        // Keyed by kind as well as declaration: Declared carries no kind, so a [Requires] and an
        // [Ensures] naming the same predicate on the same member are equal records, and one being
        // applied would mark the other applied. They move together today because the run path hands
        // both the same seam owner, but that is a coincidence of the current shape, not a property
        // of the pair.
        //
        // Every kind, not only the contracts it once held. A [Measure] never passes through
        // CheckPredicates at all — the two measure paths have their own filter — so each records its
        // own, and a type-level [Invariant] used to take the branch that recorded nothing. An
        // annotation the run could have applied and did not is only reportable because all four are
        // in here.
        var appliedAnnotations = new HashSet<(string Kind, Declared Declared)>();

        // Every type this run put in front of a contract, as a receiver or as a seam owner.
        //
        // ReportUncheckedAnnotations uses it to tell a contract that belongs to this bridge from one
        // that belongs to a different bridge in the same assembly. Deliberately not "the type of the
        // seam owner": on a swapped hand-off the driver is the wrong object, so a filter keyed on it
        // would be keyed on the very thing the report exists to catch, and the swap would pass
        // quietly. Asking whether the declaring type appears anywhere among the objects handed over
        // survives the swap — a swapped [Ensures] is still on the type that arrived as the receiver
        // — while a contract on a type this run never handled matches nothing and is left alone.
        var handedOverTypes = new HashSet<Type>();

        // Computed on the first state whose encoding is satisfiable, and null until then, so a
        // run that never got that far reports nothing rather than reporting nothing constrained.
        EncodingCoverage? encodingCoverage = null;

        // Which annotations were evaluated at all, and which of those ever checked anything. An
        // all(...) over an empty collection is true without looking at an element, so a predicate
        // that was vacuous every single time held vacuously and proved nothing.
        var evaluated = new HashSet<(string Which, Declared Declared)>();
        var checkedSomething = new HashSet<(string Which, Declared Declared)>();

        // Draws a transition reads and no step reported a value for, and values reported under a
        // name no transition reads. Accumulated across the run rather than per step: the question is
        // whether the bridge reports a draw at all, not which step forgot one.
        var unpinnedDraws = new SortedSet<string>(StringComparer.Ordinal);
        var unreadDraws = new SortedSet<string>(StringComparer.Ordinal);

        // Reported on every check rather than warned about once. Warnings do not reach a reader of a
        // green run — Passed does not consider them and the gate prints only failures — and a scan
        // that could not read the whole assembly may have missed an annotation that would have
        // failed. Silence there is the false pass this layer exists to prevent.
        if (annotations.LoaderErrors.Count > 0)
            failures.Add(new ConformanceFailure(
                FailureKind.AnnotationScanIncomplete, "<declaration>",
                $"could not load every type in {annotationAssembly.GetName().Name}, so an annotation " +
                "on one of them would have gone unread. Fix the load error, or pass an " +
                "annotationSource in an assembly that loads in full: " +
                string.Join("; ", annotations.LoaderErrors)));

        // The allowance's own version, compared against this layer's pin. A stale copy can miss a
        // renamed or reshaped attribute, and the scan would read the new name off an assembly that
        // still declares the old one — an annotation that went unread without a sound.
        //
        // Two cases, and the pre-marker one is why this is not a single comparison. A copy that
        // declares no version at all predates the marker — and that is every copy made before the
        // marker landed, so without this branch the first refresh after an upgrade was invisible to
        // the gate. A target with no annotations at all is a legitimate state and is skipped: the
        // branch keys on annotations being present, not on the assembly merely existing.
        var carriesAnnotations =
            annotations.StepMembers.Count > 0
            || annotations.DeterministicMembers.Count > 0
            || annotations.RequiresMethods.Count > 0
            || annotations.EnsuresMethods.Count > 0
            || annotations.InvariantMethods.Count > 0
            || annotations.MeasureMembers.Count > 0;

        if (carriesAnnotations && annotations.AllowanceVersion is null)
        {
            failures.Add(new ConformanceFailure(
                FailureKind.AnnotationVersionMismatch, "<declaration>",
                "the target carries Fmverify.Annotations attributes but declares no Allowance version " +
                $"marker, so its copy predates the one this layer was generated with (2026.09.1). " +
                "Refresh it with `fmverify add-annotations <path> --force`."));
        }
        else if (annotations.AllowanceVersion is { } allowanceVersion && allowanceVersion != "2026.09.1")
        {
            failures.Add(new ConformanceFailure(
                FailureKind.AnnotationVersionMismatch, "<declaration>",
                $"the target's annotation allowance is version {allowanceVersion}, but this layer was " +
                "generated with 2026.09.1. The checker matches attribute types by full " +
                "name, so a copy that predates a renamed or reshaped attribute would let it go unread. " +
                "Refresh it with `fmverify add-annotations <path> --force`."));
        }

        // The [Deterministic] declarations this run acted on, named rather than counted. The check
        // drives *the seam* twice and compares; it does not learn which declaration that seam is, so
        // folding these into the coverage count would assert an attribution nobody has. Populated
        // only here, so a non-empty list means the double drive happened.
        var seamDeclarations = new List<string>();

        if (annotations.DeterministicMembers.Count > 0 && scenarioList.Count > 0)
        {
            failures.AddRange(observer is null
                ? CheckDeterminism(stepper, scenarioList[0], maxStepsPerScenario)
                : CheckRunDeterminism(observer, scenarioList[0]));

            seamDeclarations.AddRange(annotations.DeterministicMembers
                .Select(d => $"[Deterministic] on {d.Owner.Name}.{d.DeclaredOn}"));
        }

        foreach (var scenario in scenarioList)
        {
            scenariosChecked++;
            var name = scenario?.ToString() ?? "<scenario>";

            // Configure, check the entry conditions, then run. That order is the whole of G6: a
            // [Requires] read after the run is read off a driver the run has finished with, so a
            // driver that repairs its own precondition passed. Scenario 0's driver was already
            // configured above to name the annotation assembly, and configuring runs nothing, so it
            // is reused rather than built twice.
            if (observer is not null)
            {
                var driver = scenariosChecked == 1 && firstDriver is not null
                    ? firstDriver
                    : observer.Configure(scenario);

                CheckPredicates(
                    annotations.RequiresMethods, driver!, driver!, "Requires",
                    name, FailureKind.AnnotationRunContract);

                CheckObservation(observer.Observe(scenario, driver), driver!, name);
            }

            var state = stepper.Start(scenario!);
            var stepsInScenario = 0;

            CheckState(state, name);

            while (stepper.Advance(state) is { } step)
            {
                observedLabels.Add(step.Label);

                // Bounded, because the loop advances only through what the adapter hands back. An
                // adapter over a target that mutates in place returns the state it was given, so
                // nothing ever advances and there is always another step. Unbounded, that adapter
                // does not fail — it hangs, and a CI job burns to its timeout saying nothing.
                if (stepsInScenario >= maxStepsPerScenario)
                {
                    failures.Add(new ConformanceFailure(
                        FailureKind.AdapterDidNotTerminate, name,
                        $"still producing steps after {maxStepsPerScenario}. The usual cause is an " +
                        "adapter whose Advance returns the state it was given rather than a new one — " +
                        "check that stepping does not mutate in place. Raise maxStepsPerScenario if " +
                        "this scenario genuinely takes more steps than that."));
                    break;
                }

                stepsInScenario++;
                stepsObserved++;
                CheckStep(state, step, name);
                state = step.Post;
                CheckState(state, name);
            }

            if (stepsInScenario == 0)
                // The other silently-vacuous case. One scenario that never steps proves nothing
                // about the transitions; a whole set of them makes Passed false, but a single one
                // among many would otherwise disappear.
                warnings.Add($"scenario '{name}' took no steps, so it compared nothing.");
        }

        // Which [Step] declarations this run was ever going to check: the ones on a type it handed
        // over. Held in a local rather than recomputed, so the coverage summary and what actually
        // ran cannot disagree.
        var checkableSteps = annotations.StepMembers
            .Where(declared => handedOverTypes.Any(declared.Owner.IsAssignableFrom))
            .ToArray();

        if (stepsObserved > 0)
        {
            failures.AddRange(CheckStepDeclarations(checkableSteps, observedLabels));

            // Applied means the comparison ran for it, which is exact here: every checkable
            // declaration is compared against the observed labels and either matches or is reported.
            // Unlike a [Deterministic], a [Step] is checked per declaration, which is what lets it
            // be counted rather than only named.
            foreach (var declared in checkableSteps) appliedAnnotations.Add(("Step", declared));
        }

        // A predicate that held every time without looking at anything. Reported after the loop
        // because that is when "every time" is known: one vacuous evaluation is a collection that
        // happened to be empty, and all of them is an annotation nothing could have refuted.
        foreach (var (which, declared) in evaluated.Except(checkedSomething))
        {
            failures.Add(new ConformanceFailure(
                FailureKind.AnnotationVacuous, "<declaration>",
                $"[{which}({declared.Name})] on {declared.Owner.Name}.{declared.DeclaredOn} held on " +
                "every observed state without checking anything: every all(...) in it ran over an " +
                "empty collection. It cannot fail as written, so it is not evidence. Either the " +
                "slice never populates that collection — pick one that does — or the claim belongs " +
                "on something the run produces."));
        }

        // Both shapes. The step shape was left out while it had no seam owner to name — there was
        // nothing an adopter could act on — and IStepper.SeamOwner is what gave it one.
        if (scenariosChecked > 0)
        {
            var guidance = observer is not null
                ? "No object this run's observations handed over could receive it. Either the " +
                  "observation's members are the wrong way round — [Requires] is read on Driver and " +
                  "[Ensures] on Result, both of which must come from the seam that declares them — " +
                  "or it sits on a seam this bridge does not drive."
                : "This Stepper drives that type's seam but hands over no object that owns it. " +
                  "Override IStepper.SeamOwner to return the object whose method Advance drives, or " +
                  "move the contract to the type the state is.";

            var kind = observer is not null
                ? FailureKind.AnnotationRunContract
                : FailureKind.AnnotationContract;

            ReportUncheckedAnnotations(annotations.RequiresMethods, "Requires", kind, guidance);
            ReportUncheckedAnnotations(annotations.EnsuresMethods, "Ensures", kind, guidance);

            // A type-level annotation is its declaring type's claim about its own instances, so it
            // carries none of the swapped-hand-off hypothesis the contract guidance above offers.
            // What it can be is written on a type this shape never reads that annotation on — and
            // then it was read out of the code and applied to nothing, which is the silence "unread
            // is not satisfied" forbids.
            const string SubjectGuidance =
                "Its declaring type was among the objects this run handed over, but not as a " +
                "receiver this shape reads that annotation on. [Invariant] is read on the state of a " +
                "stepping seam, and on the driver and the result of one that runs to completion; " +
                "[Measure] is read on the state per step, and across the events of its own type in a " +
                "run. Move it to a type this shape reads, or drop it. A [Measure] on a type the run " +
                "has one instance of cannot show a decrease and cannot be checked here at all.";

            ReportUncheckedAnnotations(
                annotations.InvariantMethods, "Invariant", FailureKind.AnnotationNoneApplied, SubjectGuidance);
            ReportUncheckedAnnotations(
                annotations.MeasureMembers, "Measure", FailureKind.AnnotationNoneApplied, SubjectGuidance);
        }

        return new ConformanceReport(
            failures, warnings, scenariosChecked, stepsObserved, source, Coverage(),
            // The draw lists are known only after every step has been compared, where the cell probe
            // runs on the first state — so they are folded in here rather than passed into it.
            encodingCoverage is null
                ? null
                : encodingCoverage with { UnpinnedDraws = [.. unpinnedDraws], UnreadDraws = [.. unreadDraws] });

        // The model must have a transition for this step, it must be one the model actually
        // enumerated, and that transition must be able to describe the pair the target produced.
        void CheckStep(TState concretePre, Step<TState> step, string scenarioName)
        {
            if (observer is null) CheckRequires(concretePre, scenarioName);

            // `is not { } label` rather than a null check: Label is a record in one state kind and
            // an enum in another, so MapLabel returns a nullable reference in one and a
            // Nullable<Label> in the other. Only this pattern unwraps both to a plain Label.
            if (encoding.MapLabel(step.Label) is not { } label)
            {
                failures.Add(new ConformanceFailure(
                    FailureKind.ModelIncomplete, scenarioName,
                    $"the target performed '{step.Label}' but no transition describes it. Add a " +
                    "transition to the model, or narrow the property's scope to exclude this behaviour."));
                return;
            }

            if (!TransitionRelation.Labels.Contains(label))
            {
                // Constructing a Label is not the same as the model having one: nothing was proven
                // about a label that was never enumerated, so accepting it would back this step
                // with no obligation at all.
                failures.Add(new ConformanceFailure(
                    FailureKind.LabelNotInModel, scenarioName,
                    $"MapLabel returned '{label}' for step '{step.Label}', which is not in " +
                    "TransitionRelation.Labels. No consecution or non-vacuity obligation was ever " +
                    "discharged for it, so it proves nothing. Return a label the model enumerates."));
                return;
            }

            CheckTransition(concretePre, label, step.Post, step.Label, scenarioName, step.Draws);
            if (observer is null) CheckEnsures(concretePre, step.Post, scenarioName);
            if (observer is null && (property.IsProgressLabel is null || property.IsProgressLabel(label)))
                CheckMeasure(concretePre, step.Post, scenarioName);
        }

        // Checking each state against the invariant is not enough on its own: a model whose
        // transition cannot describe the step between two individually-fine states passes every
        // per-state check. A footprint that contradicts its own effects produces exactly that.
        void CheckTransition(
            TState concretePre, Label label, TState concretePost, string stepLabel, string scenarioName,
            IReadOnlyDictionary<string, int>? reportedDraws)
        {
            using var c = new SmtContext();
            var tr = new TransitionRelation(c);
            var pre = c.FreshState("pre");
            var post = c.FreshState("post");
            var clause = tr.Clause(label, pre, post);

            var solver = c.Z3.MkSolver();
            try
            {
                solver.Assert(encoding.Encode(c, pre, concretePre));
                solver.Assert(encoding.Encode(c, post, concretePost));
            }
            catch (Exception ex)
            {
                failures.Add(new ConformanceFailure(
                    FailureKind.EncodingThrew, scenarioName,
                    $"Encode threw while mapping the states of step '{stepLabel}' onto the model: " +
                    $"{ex.Message}\nThe usual cause is a concrete state the model's state sort has no " +
                    "cell for — a population larger than the model's bound, or an index outside it. " +
                    "Fix Encode or the scenario, not the model."));
                return;
            }

            solver.Assert(clause);

            // A draw is free, so without a reported value this asks whether *some* draw explains the
            // step — which is weaker than it looks: a transition can be judged against a run the code
            // never made. Pinning it to what the code drew makes the comparison about this run.
            //
            // A draw the clause reads and the step did not report is recorded rather than failed. An
            // existing bridge predates this and must not go red on an upgrade; the coverage summary
            // is where a reader finds out the check was the weaker one.
            foreach (var name in Draws.NamesIn(c, clause))
            {
                if (reportedDraws is not null && reportedDraws.TryGetValue(name, out var drawn))
                    solver.Assert(c.Z3.MkEq(Draws.Value(c, name), c.Z3.MkInt(drawn)));
                else
                    unpinnedDraws.Add(name);
            }

            // The other direction, and a naming mistake is the likely cause: a value reported under a
            // name no transition reads is a value nothing pinned, and silence would let a bridge look
            // as though it were reporting draws while pinning none of them.
            foreach (var reported in reportedDraws?.Keys ?? [])
            {
                if (!Draws.NamesIn(c, clause).Contains(reported)) unreadDraws.Add(reported);
            }

            switch (solver.Check())
            {
                case Status.UNSATISFIABLE:
                    failures.Add(new ConformanceFailure(
                        FailureKind.TransitionNotModelled, scenarioName,
                        $"the target's '{stepLabel}' step maps to transition '{label}', but that " +
                        "transition cannot describe the states before and after it. Either the " +
                        "encoding is wrong, or the transition's guard, effects or footprint do not " +
                        "match what the code does."));
                    break;

                case Status.UNKNOWN:
                    // UNKNOWN is failure, never a pass — the same rule as everywhere else here.
                    failures.Add(new ConformanceFailure(
                        FailureKind.TransitionNotModelled, scenarioName,
                        $"solver returned UNKNOWN for transition '{label}'; UNKNOWN is failure, never a pass."));
                    break;
            }
        }

        void CheckState(TState concrete, string scenarioName)
        {
            using var c = new SmtContext();
            var f = new Formulas(c);
            var s = c.FreshState("conf");

            BoolExpr encoded;
            try { encoded = encoding.Encode(c, s, concrete); }
            catch (Exception ex)
            {
                failures.Add(new ConformanceFailure(
                    FailureKind.EncodingThrew, scenarioName,
                    $"Encode threw while mapping a concrete state onto the model: {ex.Message}\n" +
                    "The usual cause is a concrete state the model's state sort has no cell for — a " +
                    "population larger than the model's bound, or an index outside it. Fix Encode or " +
                    "the scenario, not the model."));
                return;
            }

            // Non-vacuity for the bridge, and it must come first. If the encoding is
            // self-contradictory then `encoded AND NOT Invariant` is UNSAT for ANY invariant, every
            // state passes, and the layer reports green while proving nothing. Every other way of
            // getting the encoding wrong produces a false FAILURE, which announces itself; this one
            // does not.
            var vacuity = c.Z3.MkSolver();
            vacuity.Assert(encoded);
            if (vacuity.Check() == Status.UNSATISFIABLE)
            {
                failures.Add(new ConformanceFailure(
                    FailureKind.EncodingUnsatisfiable, scenarioName,
                    "the encoding of this concrete state is self-contradictory, so every check over " +
                    "it would pass vacuously. Fix Encode: assert one equality per model state " +
                    "variable and nothing that can conflict."));
                return;
            }

            // Which cells the encoding pinned, probed once and on the first state that got this far.
            // Whether an encoding determines a cell could in principle vary per concrete state, so
            // the report says which state it describes rather than implying it holds for all of
            // them. It rides the satisfying assignment the guard above just produced.
            encodingCoverage ??= ProbeEncoding(c, s, encoded, vacuity.Model);

            if (observer is null) CheckInvariants(concrete, scenarioName);

            var solver = c.Z3.MkSolver();
            solver.Assert(encoded);
            solver.Assert(c.Z3.MkNot(property.Invariant(f, s)));

            switch (solver.Check())
            {
                // SATISFIABLE means this concrete state violates the invariant.
                case Status.SATISFIABLE:
                    failures.Add(new ConformanceFailure(
                        FailureKind.InvariantViolated, scenarioName,
                        $"invariant of '{property.Id}' does not hold in a reachable concrete state:\n{solver.Model}"));
                    break;

                case Status.UNKNOWN:
                    failures.Add(new ConformanceFailure(
                        FailureKind.InvariantViolated, scenarioName,
                        $"solver returned UNKNOWN for '{property.Id}'; UNKNOWN is failure, never a pass."));
                    break;
            }
        }

        // Every receiver here is an object the target produced. That is the whole difference between
        // this stage and the step-shape one, and the reason this stage's claim is true: a wrong
        // Stepper cannot make it pass, because the Stepper had no part in producing any of it.
        void CheckObservation(RunObservation observation, object driver, string scenarioName)
        {
            // The driver owns the seam on this shape, so it is what decides whether a member-level
            // [Ensures] is this run's obligation — while being read on the result. The result is not
            // the class that declares the seam, so deciding that from the result skipped the
            // annotation entirely. [Requires] is not here: it is checked before the run, by the
            // caller, which is what makes it mean anything.
            CheckPredicates(
                annotations.EnsuresMethods, observation.Result, driver, "Ensures",
                scenarioName, FailureKind.AnnotationRunContract);
            // Both objects the run handed over, because a type-level [Invariant] is its declaring
            // type's claim about its own instances and either of these may be one. The owner filter
            // makes the two calls disjoint — each applies only the invariants whose declaring type
            // its receiver is an instance of — so nothing is checked twice, and an invariant on the
            // driver's own type is no longer dropped for failing the filter against the result.
            CheckPredicates(
                annotations.InvariantMethods, observation.Result, driver, "Invariant",
                scenarioName, FailureKind.AnnotationRunContract);
            CheckPredicates(
                annotations.InvariantMethods, driver, driver, "Invariant",
                scenarioName, FailureKind.AnnotationRunContract);

            if (observation.Events.Count == 0)
            {
                failures.Add(new ConformanceFailure(
                    FailureKind.RunObservedNothing, scenarioName,
                    "the observation carried no events, so nothing was compared and no [Measure] was " +
                    "read. Return the events the run produced, or drop this scenario."));
                return;
            }

            CheckRunMeasures(observation.Events, scenarioName);
        }

        // A contract that was read out of the code and then checked against nothing. On this shape
        // the objects an observation hands over are untyped, so a hand-off the wrong way round
        // disables every contract check at once without any predicate ever returning false — the
        // whole set is skipped consistently and the gate goes green over an unread obligation.
        //
        // Stated as a fact about the run rather than a verdict on which object is which. Nothing
        // here can tell a swapped hand-off from a contract that belongs to a seam this bridge does
        // not drive, and "no contract matched the driver, so the hand-off is wrong" would fire
        // wrongly on the second case. The failure names both causes and leaves the choice to a
        // reader who knows which seam they wrote.
        /// <summary>
        /// What this run reached, as a fact rather than a verdict. Nothing gates on it, which is why
        /// the count may be assembly-wide: "not reached by this run" is true whoever owns the
        /// annotation, and a bridge legitimately reaches only the annotations on the types it hands
        /// over. The union across every registered check is what says whether anything was reached
        /// by nothing at all, and the gate reports that separately.
        /// </summary>
        AnnotationCoverage Coverage()
        {
            var all = new[]
            {
                ("Requires", annotations.RequiresMethods),
                ("Ensures", annotations.EnsuresMethods),
                ("Invariant", annotations.InvariantMethods),
                ("Measure", annotations.MeasureMembers),
                ("Step", annotations.StepMembers),
            };

            // Phrased as the failure detail phrases it, so an annotation met in both places reads as
            // one thing rather than two.
            var unreached = all
                .SelectMany(g => g.Item2.Select(d => (Kind: g.Item1, Declared: d)))
                .Where(x => !appliedAnnotations.Contains((x.Kind, x.Declared)))
                .Select(x => $"[{x.Kind}({x.Declared.Name})] on {x.Declared.Owner.Name}.{x.Declared.DeclaredOn}")
                .ToList();

            return new AnnotationCoverage(
                all.Sum(g => g.Item2.Count), appliedAnnotations.Count, unreached, seamDeclarations);
        }

        void ReportUncheckedAnnotations(IReadOnlyList<Declared> annotated, string which, string kind, string guidance)
        {
            foreach (var declared in annotated)
            {
                if (appliedAnnotations.Contains((which, declared))) continue;

                // A type this run never handled is a different bridge's seam, so not this bridge's
                // obligation. One target assembly can hold a run-shaped seam and a step-shaped one,
                // and reporting the other one's contracts here produced a red gate an adopter could
                // only clear by deleting an annotation that is true of their code.
                //
                // What survives is a contract on a type this run did hand over and no receiver took
                // — which is what a swapped hand-off looks like, and why the test is over the whole
                // set rather than over the seam owner alone.
                if (!handedOverTypes.Any(declared.Owner.IsAssignableFrom)) continue;

                failures.Add(new ConformanceFailure(
                    kind, "<declaration>",
                    $"[{which}({declared.Name})] on {declared.Owner.Name}.{declared.DeclaredOn} was " +
                    $"declared but never checked. {guidance}"));
            }
        }

        // Non-increasing across the sequence, and it must fall at least once. A constant sequence is
        // a failure: a measure that never falls anchors no termination claim, and accepting one would
        // make the annotation decoration. Per-event strict decrease would be wrong the other way — a
        // run emits starts as well as finishes, and remaining work only falls on the finishes.
        void CheckRunMeasures(IReadOnlyList<object> events, string scenarioName)
        {
            foreach (var declared in annotations.MeasureMembers)
            {
                var owned = 0;
                var readings = new List<int>();
                foreach (var e in events)
                {
                    handedOverTypes.Add(e.GetType());
                    if (!declared.Owner.IsInstanceOfType(e)) continue;
                    owned++;
                    if (ReadInt(e, declared.Name) is { } v) readings.Add(v);
                }

                // An event of the declaring type existed, so this measure is this run's obligation
                // whatever the readings then say. Recorded here rather than beside the readings: a
                // [Measure] naming a member nothing could read still applied — it is reported below
                // as unread, which is a different failure from never having been this run's business.
                if (owned > 0) appliedAnnotations.Add(("Measure", declared));

                // No event of the declaring type at all: the annotation is some other type's claim,
                // so it is not this run's obligation. Events of that type whose member could not be
                // read is a different thing entirely — a [Measure] naming a long, a double or a
                // field goes unread, and unread is not satisfied.
                if (owned == 0) continue;

                if (readings.Count == 0)
                {
                    failures.Add(new ConformanceFailure(
                        FailureKind.AnnotationRunMeasure, scenarioName,
                        $"[Measure({declared.Name})] does not name a readable int member on " +
                        $"{declared.Owner.Name}, so it went unread on every event of that type. It " +
                        "must be an int property, a parameterless int method, or an expression over " +
                        "them."));
                    continue;
                }

                // One reading cannot show a decrease, so accepting it would anchor the model's
                // ranking function to nothing — the same vacuity as an event list with nothing in
                // it, at a smaller number. The count is in the message because "did not fall"
                // alone would leave a reader unsure whether their sequence was rejected for rising
                // or for being too short to judge.
                if (readings.Count == 1)
                {
                    failures.Add(new ConformanceFailure(
                        FailureKind.AnnotationRunMeasure, scenarioName,
                        $"[Measure({declared.Name})] was read on 1 event, and a single reading " +
                        "cannot show a decrease. A quantity never observed to fall anchors no " +
                        "termination claim: return the events the run produced, or drop this " +
                        "scenario."));
                    continue;
                }

                // A rise ends this member's checking, not the whole stage. Each annotation is its
                // own obligation, so stopping here would silently drop every [Measure] declared
                // after this one.
                var rose = false;
                for (var i = 1; i < readings.Count && !rose; i++)
                    rose = readings[i] > readings[i - 1];

                if (rose)
                {
                    failures.Add(new ConformanceFailure(
                        FailureKind.AnnotationRunMeasure, scenarioName,
                        $"[Measure({declared.Name})] rose across the run's events: " +
                        $"{string.Join(", ", readings)}."));
                    continue;
                }

                // Two readings at least by now, and non-increasing, so first equal to last means the
                // whole sequence was flat. No count guard here: a sequence too short to judge was
                // already reported above, and a guard implying one could still arrive would read as
                // if this case tolerated it.
                if (readings[^1] == readings[0])
                    failures.Add(new ConformanceFailure(
                        FailureKind.AnnotationRunMeasure, scenarioName,
                        $"[Measure({declared.Name})] never fell across the run's events: " +
                        $"{string.Join(", ", readings)}. A quantity that does not decrease anchors no " +
                        "termination claim."));
            }
        }

        // The object whose method this Stepper drives. A Stepper whose state is that object —
        // reusing a target type as its replay state — answers null and gets the state, which is
        // what this shape did for every bridge before the member existed. Where the two differ, a
        // Stepper that answers null cannot have a member-level contract attributed to it at all,
        // and ReportUncheckedAnnotations says so rather than passing it over.
        object StepSeamOwner(TState concrete) => stepper.SeamOwner ?? concrete!;

        void CheckInvariants(TState concrete, string scenarioName) =>
            CheckPredicates(annotations.InvariantMethods, concrete!, StepSeamOwner(concrete),
                            "Invariant", scenarioName);

        void CheckRequires(TState concrete, string scenarioName) =>
            CheckPredicates(annotations.RequiresMethods, concrete!, StepSeamOwner(concrete),
                            "Requires", scenarioName);

        // The only predicate that sees two states. A postcondition is a relation between the state
        // the step was entered in and the one it produced, and old(...) is how it names the first —
        // so the entry state is threaded here and nowhere else. CheckRequires and CheckInvariants
        // supply none deliberately: both hold at a single state, and offering one would let an
        // expression name a "before" that means nothing on those.
        void CheckEnsures(TState concretePre, TState concretePost, string scenarioName) =>
            CheckPredicates(annotations.EnsuresMethods, concretePost!, StepSeamOwner(concretePost),
                            "Ensures", scenarioName, before: concretePre);

        // `receiver` is the object each predicate is read on. `seamOwner` is the object that owns
        // the annotated seam. They are separate parameters because they come apart on both shapes —
        // the driver against the result on a run, the Stepper's seam owner against the state on a
        // step — and conflating them is what silently skipped every [Ensures] on the run shape and
        // every member-level contract on a step shape whose state is not the seam's own type.
        void CheckPredicates(
            IReadOnlyList<Declared> methods, object receiver, object seamOwner, string which,
            string scenarioName, string kind = FailureKind.AnnotationContract, object? before = null)
        {
            handedOverTypes.Add(receiver.GetType());
            handedOverTypes.Add(seamOwner.GetType());

            foreach (var declared in methods)
            {
                // Whether an annotation applies here and what it is read on are two different
                // questions, and the run shape is where they come apart.
                //
                // A type-level [Invariant] or [Measure] is a claim the declaring type makes about
                // its own instances, so it is this receiver's obligation only if the receiver is one
                // of them — that filter is what lets two annotations of the same kind live on
                // different types in one assembly.
                //
                // A member-level [Requires] or [Ensures] sits on one seam, so what decides whether
                // it applies is who owns that seam, while the object it is read on is whichever one
                // the shape dictates: the driver the run was performed with, or the result. Asking the
                // receiver instead skipped every [Ensures] on the run shape in silence, because the
                // result is not the class that declares the seam; and dropping the filter for these
                // is not the fix either — one target assembly can hold a run-shaped seam and a
                // step-shaped one, and the step-shape check would then report the other seam's
                // preconditions as failures against a state that never claimed them.
                var subject = declared.OwnerIsSubject ? receiver : seamOwner;
                if (!declared.Owner.IsInstanceOfType(subject)) continue;

                // Past the filter is what "applied" means: from here the predicate either parses,
                // resolves and runs, or fails saying which of the three it did not. Recorded so that
                // an annotation nothing ever reached can be reported after the loop rather than
                // passing as silence.
                //
                // Every kind, not only the member-level contracts. A type-level [Invariant] takes
                // the other branch of the filter above and used to go unrecorded, which is why one
                // read on nothing could not be reported.
                appliedAnnotations.Add((which, declared));

                var (expression, parseError) = ReadExpression(declared.Name);
                if (expression is null)
                {
                    failures.Add(new ConformanceFailure(FailureKind.AnnotationMalformed, scenarioName,
                        $"[{which}({declared.Name})] on {declared.Owner.Name}.{declared.DeclaredOn} " +
                        $"does not parse: {parseError}"));
                    continue;
                }

                // old(...) names the state the seam was entered in, and two situations supply none.
                // A precondition or an invariant holds at one state, so there is no earlier one to
                // name; and on a seam that runs to completion the driver is handed back after the
                // run, so no pre-run object ever reaches the checker. Both are refused rather than
                // resolved against the object at hand, which would make the predicate quietly true
                // of the wrong state. The failure takes this call site's kind, not
                // AnnotationMalformed: the expression parsed, and what it ran into is the shape.
                if (expression.UsesOld && before is null)
                {
                    failures.Add(new ConformanceFailure(kind, scenarioName,
                        $"[{which}({declared.Name})] uses old(...), which names the state the seam was " +
                        "entered in, and nothing here supplies one. A precondition and an invariant " +
                        "hold at a single state; a seam that runs to completion hands its driver back " +
                        "afterwards. State this over the receiver alone, or move it to an [Ensures] on " +
                        "a stepping seam."));
                    continue;
                }

                var outcome = expression.Evaluate(receiver, before);
                if (outcome.Value is not bool holds)
                {
                    // Covers both a name that resolves to nothing and one that resolves to the wrong
                    // thing, which is what the single message here used to say for the only form
                    // there was. The evaluator's own reason is more specific than this call site can
                    // be, so it is carried rather than restated.
                    failures.Add(new ConformanceFailure(kind, scenarioName,
                        $"[{which}({declared.Name})] could not be evaluated on " +
                        $"{receiver.GetType().Name}: {outcome.Failure}"));
                    continue;
                }

                // Whether an all(...) in it had anything to look at. Counted per annotation across
                // the whole run: one vacuous evaluation is legitimate — a collection that happens to
                // be empty for one scenario — and every one of them means the annotation is
                // decoration. Same rule [Measure] already applies with "never fell".
                var seen = (which, declared);
                evaluated.Add(seen);
                if (!outcome.Vacuous) checkedSomething.Add(seen);

                if (!holds)
                    failures.Add(new ConformanceFailure(kind, scenarioName,
                        $"the [{which}({declared.Name})] predicate returned false on an observed state."));
            }
        }

        void CheckMeasure(TState pre, TState post, string scenarioName)
        {
            foreach (var declared in annotations.MeasureMembers)
            {
                // The annotation belongs to another type in the same assembly. Not this receiver's
                // claim, so not this receiver's obligation — and reading it here would fail a lookup
                // that was never asked for. Unconditional, unlike the same filter in
                // CheckPredicates: a [Measure] is always its declaring type's claim about its own
                // instances, whether it was written on the type or on one of its members.
                handedOverTypes.Add(pre!.GetType());
                if (!declared.Owner.IsInstanceOfType(pre)) continue;
                appliedAnnotations.Add(("Measure", declared));

                var value = ReadInt(pre, declared.Name);
                if (value is null)
                {
                    failures.Add(new ConformanceFailure(FailureKind.AnnotationMeasure, scenarioName,
                        $"[Measure({declared.Name})] did not read an int off the state. It must be an " +
                        "int property, a parameterless int method, or an expression over them."));
                    continue;
                }
                var after = ReadInt(post, declared.Name);
                if (after is not null && after >= value)
                    failures.Add(new ConformanceFailure(FailureKind.AnnotationMeasure, scenarioName,
                        $"[Measure({declared.Name})] did not decrease: {value} -> {after}."));
            }
        }

        /// <summary>
        /// The int a <c>[Measure]</c> names, or null where it could not be read.
        ///
        /// <para>Null is what both call sites already treat as "went unread, and unread is not
        /// satisfied", so an expression that does not parse, names nothing, or yields a bool needs
        /// no failure kind of its own here: it arrives at the report those sites already write. The
        /// name and signature are unchanged for the same reason.</para>
        /// </summary>
        int? ReadInt(object receiver, string name)
        {
            var (expression, _) = ReadExpression(name);

            return expression?.Evaluate(receiver, before: null).Value as int?;
        }
    }
}
