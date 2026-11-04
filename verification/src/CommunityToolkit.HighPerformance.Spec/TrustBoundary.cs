using System.Text;
using System.Text.Json;

namespace CommunityToolkit.HighPerformance.Spec;

/// <summary>
/// Renders TRUST-BOUNDARY.md from the registry. The registry is the single source of truth;
/// the markdown is derived from it. The proof gate fails when the committed file and the
/// registry disagree, so a trust boundary that nobody regenerated cannot ship — a document
/// that can be hand-edited to claim a backing the layer does not have is worse than no
/// document at all.
/// </summary>
public static class TrustBoundary
{
    public const string FileName = "TRUST-BOUNDARY.md";

    /// <summary>
    /// Takes no solver argument on purpose. Reading <see cref="SolverInfo"/> directly means a
    /// document citing a version divorced from its hash is not expressible.
    /// </summary>
    public static string Render(ICapabilityRegistry registry) => Render(registry, null);

    public static string Render(ICapabilityRegistry registry, string? scaffoldRoot)
    {
        // Ordinal-ordered so the output does not depend on incidental registry order: this
        // string is compared against a committed file.
        var properties = registry.AllEncoded()
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();

        var modelOnly = properties.Where(p => p.Backing == TrustBacking.ModelOnly).ToArray();
        var conformanceBacked = properties.Where(p => p.Backing == TrustBacking.ConformanceBacked).ToArray();
        var translationEvidencePath = LocateTranslationEvidence(scaffoldRoot);
        var translationClaims = translationEvidencePath is not null
            ? ReadTranslationClaims(translationEvidencePath)
            : Array.Empty<TranslationClaimInfo>();

        var sb = new StringBuilder();
        sb.Append("# Trust boundary — CommunityToolkit.HighPerformance verification layer\n\n");
        sb.Append("<!--\n");
        sb.Append("  GENERATED from the capability registry. Do not hand-edit.\n");
        sb.Append("  Regenerate: dotnet run --project src/CommunityToolkit.HighPerformance.Spec.Descriptor -- --emit-trust-boundary\n");
        sb.Append("  The proof gate fails if this file and the registry disagree.\n");
        sb.Append("-->\n\n");

        if (translationClaims.Count > 0)
        {
            if (conformanceBacked.Length == 0)
            {
                sb.Append($"**No property in this layer constrains CommunityToolkit.HighPerformance's code via bridge. {translationClaims.Count} claims are directly proven from translated source code. The rest constrain the model only.**\n\n");
            }
            else
            {
                sb.Append($"**{conformanceBacked.Length} of {properties.Length} properties constrain CommunityToolkit.HighPerformance's code via bridge. {translationClaims.Count} claims are directly proven from translated source code. The rest constrain the model only.**\n\n");
            }
        }
        else
        {
            sb.Append(conformanceBacked.Length == 0
                ? "**No property in this layer constrains CommunityToolkit.HighPerformance's code.**\n\n"
                : $"**{conformanceBacked.Length} of {properties.Length} properties constrain CommunityToolkit.HighPerformance's code. The rest constrain the model only.**\n\n");
        }

        AppendSolverProvenance(sb);
        if (properties.Any(p => p.Engine == ProofEngine.Unbounded))
            AppendUnboundedEngineProvenance(sb);

        sb.Append("## Model-only properties\n\n");
        sb.Append("Proven about an independent, hand-written model. Nothing here proves that\n");
        sb.Append("CommunityToolkit.HighPerformance behaves like that model — for these, a green gate means the model is\n");
        sb.Append("internally consistent and satisfies the stated invariants, and no more.\n\n");
        AppendProperties(sb, modelOnly);

        sb.Append("## Conformance-backed properties\n\n");
        if (conformanceBacked.Length == 0)
        {
            sb.Append("None. No conformance bridge exists yet, so no property here can claim one.\n\n");
        }
        else
        {
            sb.Append("Checked against the target's observable behaviour, within the bound each one states.\n");
            sb.Append("The adapter and the state encoding they rely on are **hand-written and unverified**:\n");
            sb.Append("a mistake in either weakens every claim in this section. Where a backing was\n");
            sb.Append("earned on the run shape, the run observer that hands a run's own objects to the\n");
            sb.Append("checker is a third such file: a hand-off the wrong way round is reported only\n");
            sb.Append("where the target declares a contract on the seam, and `ADOPTION.md` names the\n");
            sb.Append("case that goes unreported.\n\n");
            AppendProperties(sb, conformanceBacked);
        }

        if (translationClaims.Count > 0)
        {
            sb.Append("## Translated code claims\n\n");
            sb.Append("Proven directly about CommunityToolkit.HighPerformance's source code by translating annotated C# into formal specifications:\n\n");
            sb.Append("| Owner / Member | Contract | Expression | Engine | Semantics | Status |\n");
            sb.Append("| :--- | :--- | :--- | :--- | :--- | :--- |\n");
            foreach (var claim in translationClaims)
            {
                sb.Append($"| {CodeCell(claim.Owner)} | {TextCell(claim.Kind)} | {CodeCell(claim.Expression)} | {TextCell(claim.Engine)} | {TextCell(claim.Semantics)} | {TextCell(claim.Status)} |\n");
            }
            sb.Append("\n");
        }

        sb.Append("## Standing caveats\n\n");
        sb.Append("- `UNKNOWN` from the solver is failure, never a proof.\n");
        sb.Append("- A green gate means exactly what each property's words and scope say — no more.\n");
        sb.Append("- Add properties with the `verified-properties` skill; they enrol in the gate by\n");
        sb.Append("  appearing in the registry, and this document is regenerated from it.\n");

        return sb.ToString();
    }

    /// <summary>
    /// Ids of properties declaring a trust backing this layer cannot justify. Empty is the
    /// passing state.
    ///
    /// <para>Lives here rather than on the descriptor because both surfaces that must reject an
    /// over-claim — the proof gate and the descriptor — reference Spec, and only one of them
    /// can reference the descriptor. One rule, one implementation.</para>
    ///
    /// <para><paramref name="checks"/> is the evidence the caller actually has, and passing an
    /// empty list is a statement rather than an omission — it means "I cannot verify conformance",
    /// which is why there is no default. The descriptor passes empty always: it emits a document
    /// without running the target, so it is in no position to certify a claim about the target's
    /// behaviour. The proof gate passes the checks the conformance project supplies, and is the only
    /// surface that can justify this backing.</para>
    ///
    /// <para>A check that exists is not enough — it has to have <b>passed</b>. A property may not
    /// keep a backing whose evidence has gone red, and <see cref="ConformanceReport.Passed"/> is
    /// itself stricter than "no failures": a run that compared nothing does not justify
    /// anything.</para>
    /// </summary>
    public static IReadOnlyList<string> UnjustifiedBackings(
        ICapabilityRegistry registry,
        IReadOnlyList<ConformanceCheck> checks)
    {
        // A Liveness property is refused a backing whatever evidence exists, and the check above is
        // not a formality. The conformance checker compares encoded states against the invariant and
        // observed steps against the transition relation; it knows nothing about ranking measures. A
        // check registered for a termination property therefore PASSES -- it is checking the safety
        // half -- while establishing nothing whatever about termination, and without this clause the
        // claim is promoted on that evidence.
        //
        // That failure is quiet and it flatters: it raises the ratio in the very document written to
        // stop a layer claiming more than it checked, and a reader who sees ConformanceBacked stops
        // reading there. It is also exactly what someone under pressure to "get every property
        // backed" would do first -- and a termination claim is the one property in the layer that
        // no trace can ever witness, since every observed run is finite and terminated.
        //
        // An Unbounded claim is refused for the same reason in a different shape: the checker runs
        // finite scenarios, and no finite run can back a claim made for every size.
        //
        // Each check runs once, here, rather than once per property that looks it up.
        var evidence = checks
            .GroupBy(c => c.PropertyId)
            .ToDictionary(g => g.Key, g => g.All(c => c.Run().Passed));

        return registry.AllEncoded()
            .Where(p => p.Backing == TrustBacking.ConformanceBacked)
            .Where(p => p.Kind == PropertyKind.Liveness
                        || p.Engine == ProofEngine.Unbounded
                        || !evidence.TryGetValue(p.Id, out var passed) || !passed)
            .Select(p => p.Id)
            .ToArray();
    }

    /// <summary>
    /// Ids of encoded properties naming an engine that did not discharge them. Empty is the
    /// passing state.
    ///
    /// <para>Two ways to get it wrong, both of which promote a claim by assertion. A property
    /// projected from a <see cref="PropertyDefinition"/> was discharged by the bounded
    /// <see cref="Verifier"/>, whatever engine its encoding names. And a property whose engine
    /// differs from its class's was not discharged by the machinery its class advertises.</para>
    ///
    /// <para>A <see cref="EncodedProperty.ClassId"/> naming no supported class is not this rule's
    /// concern: with no class there is no engine to disagree with.</para>
    /// </summary>
    public static IReadOnlyList<string> MisattributedEngines(ICapabilityRegistry registry)
    {
        var definitions = registry.Properties.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var classEngines = registry.AllClasses()
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Engine, StringComparer.Ordinal);

        return registry.AllEncoded()
            .Where(p => (definitions.Contains(p.Id) && p.Engine != ProofEngine.Bounded)
                        || (classEngines.TryGetValue(p.ClassId, out var engine) && engine != p.Engine))
            .Select(p => p.Id)
            .ToArray();
    }

    /// <summary>
    /// The committed document, found by ascending to the scaffold root — the directory holding
    /// the generated solution — and taking the file beside it. The gate and the descriptor's
    /// emit mode both call this, from different output directories, so both resolve one file.
    ///
    /// <para>Anchored on the solution rather than on "the first TRUST-BOUNDARY.md found going
    /// up", which would happily pick up an unrelated one above the scaffold and compare this
    /// registry against a document belonging to something else.</para>
    /// </summary>
    public static string LocateFile()
    {
        const string solution = "CommunityToolkit.HighPerformance.Verification.slnx";

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, solution))) continue;

            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;

            throw new FileNotFoundException(
                $"Scaffold root '{dir.FullName}' has no {FileName}. Regenerate it with " +
                "--emit-trust-boundary.");
        }

        throw new FileNotFoundException($"No scaffold root ({solution}) above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// Locates the translation evidence file (verification/translation/translation-evidence.json)
    /// if present in the scaffold or under the specified root directory.
    /// </summary>
    public static string? LocateTranslationEvidence(string? scaffoldRoot = null)
    {
        const string solution = "CommunityToolkit.HighPerformance.Verification.slnx";

        if (scaffoldRoot is not null)
        {
            var candidate = Path.Combine(scaffoldRoot, "translation", "translation-evidence.json");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(scaffoldRoot, "verification", "translation", "translation-evidence.json");
            return File.Exists(candidate) ? candidate : null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, solution))) continue;

            var candidate = Path.Combine(dir.FullName, "translation", "translation-evidence.json");
            return File.Exists(candidate) ? candidate : null;
        }

        return null;
    }

    private static IReadOnlyList<TranslationClaimInfo> ReadTranslationClaims(string evidencePath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(evidencePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An absent evidence file and an unreadable one are different facts. Returning an
            // empty list here would emit a document that silently drops claims it used to
            // publish, which is the opposite of what a trust boundary is for.
            throw new InvalidOperationException(
                $"Could not read translation evidence '{evidencePath}': {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("claims", out var claimsProp))
                return Array.Empty<TranslationClaimInfo>();

            var list = new List<TranslationClaimInfo>();
            foreach (var elem in claimsProp.EnumerateArray())
            {
                var id = elem.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "";
                var owner = elem.TryGetProperty("owner", out var pOwner) ? pOwner.GetString() ?? "" : "";
                var kind = elem.TryGetProperty("kind", out var pKind) ? pKind.GetString() ?? "" : "";
                var expr = elem.TryGetProperty("expression", out var pExpr) ? pExpr.GetString() ?? "" : "";
                var engine = elem.TryGetProperty("engine", out var pEng) ? pEng.GetString() ?? "" : "";
                var semantics = elem.TryGetProperty("semantics", out var pSem) ? pSem.GetString() ?? "" : "";
                var status = elem.TryGetProperty("status", out var pStat) ? pStat.GetString() ?? "" : "";

                list.Add(new TranslationClaimInfo(id, owner, kind, expr, engine, semantics, status));
            }

            return list
                .OrderBy(c => c.Owner, StringComparer.Ordinal)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// A Markdown table cell. A newline ends the row and a raw pipe starts a new column, so both
    /// are neutralised; GFM resolves <c>\|</c> back to a pipe even inside a code span.
    /// </summary>
    private static string TextCell(string value) =>
        value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace("|", "\\|");

    /// <summary>
    /// A cell whose content is a code span. A backtick in the content cannot be backslash-escaped
    /// inside a span, so the delimiter widens instead.
    /// </summary>
    private static string CodeCell(string value)
    {
        var text = TextCell(value);
        return text.Contains('`') ? $"`` {text} ``" : $"`{text}`";
    }

    private sealed record TranslationClaimInfo(
        string Id,
        string Owner,
        string Kind,
        string Expression,
        string Engine,
        string Semantics,
        string Status);

    /// <summary>
    /// Line endings are normalised: the committed file goes through git, so it may come back
    /// CRLF while <see cref="Render"/> emits LF. Without this the gate would fail on every
    /// Windows checkout for a reason that has nothing to do with the proofs.
    /// </summary>
    public static bool Matches(string committed, string rendered) =>
        Normalise(committed) == Normalise(rendered);

    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n');

    private static void AppendSolverProvenance(StringBuilder sb)
    {
        // Version, source and hash together, never a version alone. The package is fetched
        // from a release asset rather than restored from a feed, and the hash is what makes
        // that safe; citing the version on its own would drop the part that can be checked.
        sb.Append("## Which solver discharged these proofs\n\n");
        sb.Append("A proof is a proof by a particular solver at a particular version, built from\n");
        sb.Append("particular bytes. Cite all three when you cite a result here.\n\n");
        sb.Append($"- **Solver:** `Microsoft.Z3` **{SolverInfo.PinnedZ3Version}**\n");
        sb.Append($"- **Source:** {SolverInfo.PinnedZ3SourceUrl}\n");
        sb.Append($"- **SHA-256:** `{SolverInfo.PinnedZ3Sha256}`\n\n");
        sb.Append("The package is fetched rather than restored from nuget.org, because publishing of\n");
        sb.Append("`Microsoft.Z3` there stopped at 4.12.2 in 2023 — and 4.12.2 ships no `linux-x64`\n");
        sb.Append("native library at all, so a gate built on it cannot run on most CI.\n");
        sb.Append("`fetch-solver.sh` / `fetch-solver.ps1` verify the hash above before the package is\n");
        sb.Append("used, and refuse it on a mismatch rather than warning.\n\n");
        sb.Append("This version is fixed in the generated files. It does not track upstream: a gate\n");
        sb.Append("whose solver moved on its own could go from green to red with no change to your\n");
        sb.Append("model, and two runs of the same proof could be discharged by different solvers.\n");
        sb.Append("Re-run `fmverify init` with `--z3-version=<v>` or `--z3-version=latest` to move it\n");
        sb.Append("deliberately.\n\n");
    }

    private static void AppendUnboundedEngineProvenance(StringBuilder sb)
    {
        // Version and per-platform hashes are pinned in fetch-dafny.sh rather than repeated here: the
        // zip differs per platform, so one hash in this document would describe one machine's run.
        sb.Append("## Which engine discharged the unbounded claims\n\n");
        sb.Append($"- **Engine:** Dafny **{DafnyEngine.PinnedVersion}**, with the Z3 its release bundles\n");
        sb.Append("- **Source:** https://github.com/dafny-lang/dafny/releases\n");
        sb.Append("- **SHA-256:** one per platform, pinned in `fetch-dafny.sh` / `fetch-dafny.ps1`\n\n");
        sb.Append("Claims marked *unbounded* below hold for every size, about the Dafny source they\n");
        sb.Append("name. That source is a second hand-written model: nothing checks it against the Z3\n");
        sb.Append("model or against the target, so an unbounded claim is model-only by construction.\n\n");
    }

    private static void AppendProperties(StringBuilder sb, IReadOnlyList<EncodedProperty> properties)
    {
        if (properties.Count == 0)
        {
            sb.Append("None.\n\n");
            return;
        }

        foreach (var p in properties)
        {
            // Only the exception is marked, so a layer with no unbounded claim renders as it always did.
            var engine = p.Engine == ProofEngine.Unbounded ? ", unbounded" : "";
            sb.Append($"- `{p.Id}` ({p.Kind}{engine}, via `{p.ClassId}`) — {p.Meaning}\n");
            if (p.Scope is null)
            {
                sb.Append("  - Scope: **not stated.** Its reach is unrecorded; treat the claim as unbounded only if you have checked.\n");
                continue;
            }

            AppendScopeLine(sb, "Bound", p.Scope.Bound);
            AppendScopeLine(sb, "Preconditions", p.Scope.Preconditions);
            AppendScopeLine(sb, "Arithmetic", p.Scope.ArithmeticDomain);
            AppendScopeLine(sb, "Notes", p.Scope.Notes);
        }

        sb.Append('\n');
    }

    private static void AppendScopeLine(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.Append($"  - {label}: {value}\n");
    }
}
