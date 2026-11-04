# Trust boundary — CommunityToolkit.HighPerformance verification layer

<!--
  GENERATED from the capability registry. Do not hand-edit.
  Regenerate: dotnet run --project src/CommunityToolkit.HighPerformance.Spec.Descriptor -- --emit-trust-boundary
  The proof gate fails if this file and the registry disagree.
-->

**No property in this layer constrains CommunityToolkit.HighPerformance's code.**

## Which solver discharged these proofs

A proof is a proof by a particular solver at a particular version, built from
particular bytes. Cite all three when you cite a result here.

- **Solver:** `Microsoft.Z3` **5.3.3**
- **Source:** https://github.com/Z3Prover/z3/releases/download/z3-5.3.3/Microsoft.Z3.5.3.3.nupkg
- **SHA-256:** `3ece54cefce4d58be1ce7c00bcff34b691e8076169cba095a057284bfb0b7822`

The package is fetched rather than restored from nuget.org, because publishing of
`Microsoft.Z3` there stopped at 4.12.2 in 2023 — and 4.12.2 ships no `linux-x64`
native library at all, so a gate built on it cannot run on most CI.
`fetch-solver.sh` / `fetch-solver.ps1` verify the hash above before the package is
used, and refuse it on a mismatch rather than warning.

This version is fixed in the generated files. It does not track upstream: a gate
whose solver moved on its own could go from green to red with no change to your
model, and two runs of the same proof could be discharged by different solvers.
Re-run `fmverify init` with `--z3-version=<v>` or `--z3-version=latest` to move it
deliberately.

## Model-only properties

Proven about an independent, hand-written model. Nothing here proves that
CommunityToolkit.HighPerformance behaves like that model — for these, a green gate means the model is
internally consistent and satisfies the stated invariants, and no more.

- `seed-invariant` (Safety, via `safety.inductive-invariant`) — SEED trivial invariant (true). Replace with a real property; see verified-properties.
  - Bound: one integer state variable
  - Preconditions: none - the model has no guards
  - Arithmetic: integer
  - Notes: Trivially true (Invariant = Safety = true). This proves nothing about any real system; it exists to make the proof pipeline green from the start.

## Conformance-backed properties

None. No conformance bridge exists yet, so no property here can claim one.

## Standing caveats

- `UNKNOWN` from the solver is failure, never a proof.
- A green gate means exactly what each property's words and scope say — no more.
- Add properties with the `verified-properties` skill; they enrol in the gate by
  appearing in the registry, and this document is regenerated from it.
