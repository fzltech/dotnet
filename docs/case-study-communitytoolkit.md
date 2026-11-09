# Case study: fmverify on the .NET Community Toolkit

**Target:** [`CommunityToolkit/dotnet`](https://github.com/CommunityToolkit/dotnet) — the .NET
Community Toolkit, MIT-licensed and maintained by Microsoft / the .NET Foundation.
**Fork:** [`fzltech/dotnet`](https://github.com/fzltech/dotnet), branch `adoption/properties`.
**Issue:** [fzltech/fmverify#27](https://github.com/fzltech/fmverify/issues/27).
**Date:** 2026-11-04; updated 2026-11-05 after
[fzltech/fmverify#29](https://github.com/fzltech/fmverify/pull/41), and 2026-11-07 after the
adoption-friction fixes.
**fmverify:** `0.1.0`, annotations `2026.09.2`, Z3 `5.3.3`.

This is the first adoption of fmverify onto a real, external C# library rather than one of its
own fixtures. The goal was not a big proof; it was to find out what the first hour actually costs,
what the tool can prove on foreign code, and whether it catches a real defect.

It caught one.

---

## What was proven

Four source-level claims, in
[`src/CommunityToolkit.HighPerformance`](https://github.com/fzltech/dotnet/tree/adoption/properties/src/CommunityToolkit.HighPerformance).
All four are `int`-only, call-free arithmetic seams the toolkit already uses. The annotations come
from the `Fmverify.Annotations` package reference; this branch does not add a copied
`Fmverify.Annotations.cs`.

| Claim | Where | Statement |
| --- | --- | --- |
| `SliceArithmetic.GetPitch` | `Memory/Internals/SliceArithmetic.cs` | Adding a slice's column offset to the backing pitch is exact and non-negative for views up to 1024 wide with pitch up to 1024. |
| `SliceArithmetic.GetColumnOffset` | same | Adding a column to a non-negative row offset is exact and at least the row offset, for offsets up to 1,000,000. |
| `SliceArithmetic.GetLastIndex` | same | For a positive length, `length - 1` is non-negative and exact. Used by the constructor's checked index-size guard. |
| `ParallelArithmetic.GetBatchStop` | `Helpers/ParallelArithmetic.cs` | For a non-negative batch start at or before `end`, the batch stop is between the start and `end`, and the addition cannot overflow. |

Each is checked by the generated `SymbolicMethodChecks` gate: preconditions are satisfiable,
32-bit execution agrees with ideal arithmetic, and the postcondition holds for every input in the
precondition. `verify.sh` is green with all four.

The contracts are written as separate `[Requires]` and `[Ensures]` declarations — the form
[fzltech/fmverify#36](https://github.com/fzltech/fmverify/issues/36) restored — and the checker
conjoins them, so the proof is over the same conjunction as before.

```bash
cd verification
bash ./fetch-solver.sh
bash ./verify.sh
# ...
# Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15
```

The claims are deliberately bounded. fmverify's F5 engine has no `long`, no division, and no
method calls, so a claim about the toolkit's full `int.MaxValue` range is not expressible today.
The bounds are in the `[Requires]` strings and in the case study, not hidden in prose.

---

## The defect

`ParallelHelper.For` splits a range into batches and runs them with `Parallel.For`. The batch
runner computed its stop index as:

```csharp
int offset = i * this.batchSize;
int low = this.start + offset;
int high = low + this.batchSize;   // overflow
int stop = Math.Min(high, this.end);
```

When the last batch's `low + batchSize` passes `int.MaxValue`, `high` wraps negative, `Math.Min`
returns that negative value, and the entire last batch is skipped. The loop does not throw and does
not log; it silently does less work than the caller asked for.

The regression test uses a small range, not a 2-billion-iteration one:

```csharp
const int count = 1001;
int start = int.MaxValue - count;
int end = int.MaxValue;

ParallelHelper.For(start, end, new CountingAction(counts), 1);

Assert.Equal(count, counts[0]);
```

Before the fix, on a 4-core machine:

```
Expected: 1001
Actual:   753
```

The missing 248 invocations are the final batch. On a machine with more cores the missing share is
larger; the defect gets worse with more parallelism.

**Fix.** Counts and offsets are now computed in `long`, the batch start is narrowed back to `int`
after the arithmetic (it is always a valid index), a batch that starts at or past `end` returns
without invoking anything, and the stop is computed by `ParallelArithmetic.GetBatchStop` on the
proved non-negative path or by `long` arithmetic on the negative path. The same change also fixes
the related upstream overflow in
[CommunityToolkit/dotnet#1188](https://github.com/CommunityToolkit/dotnet/issues/1188), where
`Math.Abs(start - end)` overflows for ranges wider than `int.MaxValue` and throws for
`[int.MinValue, 0)`.

The regression tests in
[`AdoptionFindingsTests.cs`](https://github.com/fzltech/dotnet/blob/adoption/properties/verification/test/CommunityToolkit.HighPerformance.Verification/AdoptionFindingsTests.cs)
cover the end-at-`int.MaxValue` range, a range that starts below zero, and a range that starts at
`int.MinValue`. The target's own unit tests were also run for `Memory2D` and `ParallelHelper`
(72 tests on each of net8.0, net9.0, and net10.0) and pass.

---

## What fmverify refused, and why that matters

These are the constructs the first hour ran into. They are the honest limits of the current
engine, not bugs in the target.

1. **Nonlinear bit-vector multiplication does not terminate.** `GetShift` was originally
   `((width + pitch) * row) + column` with a postcondition comparing it to
   `(width + pitch) * height`. Z3 ran for more than 15 minutes and had to be killed. The same
   method with only `result >= 0` also hung. The engine accepts `*` syntactically, but a product of
   two symbolic variables is not a practical F5 obligation today. The claim was split: the
   multiplication stays inline, and only the linear addition is annotated.
2. **No `long`, no `decimal`, no division.** The correct fix for the full-range count is `long`;
   F5 cannot state or prove it. The fix is covered by conformance tests, not by the symbolic
   checker.
3. **No method calls.** `Math.Min`, `Math.Abs`, `Max`, and `checked` are outside the subset. The
   seams were extracted so the checker sees only operators.
4. **Multiple `[Ensures]` attributes do not compile.** The annotations are declared
   `[AttributeUsage(AttributeTargets.Method)]` without `AllowMultiple = true`, although the checker
   and its tests are written for multiple `[Requires]`/`[Ensures]`. The claims use one combined
   `&&` expression instead. This is a one-line fix in `Fmverify.Annotations`.
5. **The source copy is not analyzer-clean.** `fmverify add-annotations --source` writes
   `Fmverify.Annotations.cs` into the target. The toolkit's Release build treats CS1591 (missing XML
   doc) and IDE0073 (file header) as errors. A project-local `.editorconfig` excludes just that file.
   A generated file header and XML docs in the source copy would remove the manual step. The split
   adoption branch uses the package route instead: `Fmverify.Annotations` is a `PackageReference`,
   the source copy is absent, and the package's analyzer is available at build time. This remains a
   real limitation only for the air-gapped `--source` fallback.
6. **The coverage line is misleading.** The gate prints “the target declares no `[Requires]`,
   `[Ensures]`, … annotation” when there are no registered conformance checks, even though the
   symbolic checker just proved four `[Requires]`/`[Ensures]` methods. The sentence conflates
   “no annotations reached by a conformance check” with “no annotations at all.” This is tracked as
   [#39](https://github.com/fzltech/fmverify/issues/39). After #29, `fmverify audit` reads the
   gate's `symbolic-coverage.json` and lists those four claims as proven; the gate's own line still
   needs the wording fix.

---

## What the proof-coverage report says

When this adoption started, fmverify could not report per-method proof coverage. The gate said
`15 tests passed` and the bridge coverage line said the target declared no annotations. After
[#29](https://github.com/fzltech/fmverify/pull/41) merged, the same layer can be measured:

```bash
cd verification
bash ./verify.sh   # now also writes symbolic-coverage.json
fmverify audit .. --target-project=CommunityToolkit.HighPerformance \
  --assembly=src/CommunityToolkit.HighPerformance/bin/Release/net8.0/CommunityToolkit.HighPerformance.dll
```

The report says:

- **214 public methods** are in the proof surface, and **0** have a public method claim.
- The four symbolic claims are **proven**, but they sit on internal/private seams
  (`SliceArithmetic`, `ParallelArithmetic`), so the report lists them under
  **“Claims outside the public surface”** instead of counting them in the public headline.
- The largest public gaps are `BitHelper` (18), `Span2D` (15), `Memory2D` (13),
  `ReadOnlyMemory2D` (13), `ReadOnlySpan2D` (13), `ReadOnlySpanExtensions` (13),
  `SpanExtensions` (12), `MemoryOwner` (9), `RefEnumerable` (9), and `SpanOwner` (8).
- Inputs: `symbolic coverage present`; translation manifest and evidence absent.

That is the useful distinction the old gate could not make: the layer has **four proven claims and
zero public method claims**. The next adoption work is therefore not “write another proof” but
“pick a public seam”; the report names the largest ones. It also explains why the headline is
`0/214` rather than `4/214` — the four claims are honest evidence, but they are not claims about the
public surface.

The command above points `--assembly` at the `net8.0` target because the layer was repaired to
target `net8.0` (see the framework friction below). The default audit would read the target's first
TFM, `netstandard2.0`, and then warn that the layer's evidence is stale.

Write the first report as a baseline and compare against it later:

```bash
fmverify audit .. --target-project=CommunityToolkit.HighPerformance \
  --assembly=src/CommunityToolkit.HighPerformance/bin/Release/net8.0/CommunityToolkit.HighPerformance.dll \
  --json > verification/proof-coverage.json

# later
fmverify audit .. --target-project=CommunityToolkit.HighPerformance \
  --assembly=src/CommunityToolkit.HighPerformance/bin/Release/net8.0/CommunityToolkit.HighPerformance.dll \
  --baseline=verification/proof-coverage.json
```

The first comparison is all zeroes, as it should be. Once a public seam is annotated and claimed,
the next run shows the proven-method count and the closed gap by type. The baseline is deliberately
not committed here: it is a report about one run, and the commands above are what reproduce it.

## Time and friction

Measured in this session, from cloning the fork to the first green gate with four claims:

- **~10 minutes** to clone, build `CommunityToolkit.HighPerformance`, scaffold (`init`), add the
  bridge, and run the first green seed gate.
- **~5 minutes** of scaffold repair before that gate could run: `init` chose the target's first
  target framework (`netstandard2.0`) for the Spec, Descriptor, Test, and Conformance projects.
  That produces C# 7.3 against `Nullable=enable`, a test host that rejects `netstandard2.0`, and a
  `net8.0` app with no `net8.0` runtime on this machine. Setting the layer's projects to `net8.0`
  and adding `RollForward=Major` fixed it.
- **~15 minutes** lost to the nonlinear `GetShift` query before it was killed and split.
- **~5 minutes** to add the four claims and the regression tests once the linear seams were chosen.

**Time to first verified claim: ~25 minutes after `init`**, including the scaffold repairs. A
second adoption on the same machine, with the scaffold fixes in place, should be under 15 minutes.

The work that took the longest was not writing the contracts; it was deciding what to prove. The
tool cannot see inside a generic method that calls `Math.Abs`, `Math.Min`, and `checked`. The
adopter has to find a pure arithmetic seam, and the first obvious seam (the 2D index multiplication)
is the one Z3 cannot discharge.

---

## Reproduce

```bash
git clone https://github.com/fzltech/dotnet.git
cd dotnet
git checkout adoption/properties

# The target
dotnet build src/CommunityToolkit.HighPerformance/CommunityToolkit.HighPerformance.csproj -c Release

# The layer
cd verification
bash ./fetch-solver.sh
bash ./verify.sh

# The regression tests on their own
dotnet test test/CommunityToolkit.HighPerformance.Verification/CommunityToolkit.HighPerformance.Verification.csproj \
  -c Release --filter "FullyQualifiedName~AdoptionFindingsTests"
```

The layer writes only under `verification/`. The target changes are confined to the arithmetic
seams (`SliceArithmetic.cs`, `ParallelArithmetic.cs`), the two `Slice` call sites, the batch runner,
and the `OverflowHelper` call site. The annotation allowance arrives as one
`Fmverify.Annotations` `PackageReference` in `CommunityToolkit.HighPerformance.csproj`; this branch
does not add a copied annotation file or its `.editorconfig` workaround. The 0.3.3 package is
vendored under `fmverify-packages/` so the branch restores without feed credentials; an adopter
uses the GitHub Packages source instead.

---

## Follow-ups filed

- [#35](https://github.com/fzltech/fmverify/issues/35) — `init`/`add-bridge` choose the target's
  first TFM for generated projects, producing an unrunnable layer on a multi-target target (also
  covers the missing `RollForward` / installed-runtime choice).
- [#36](https://github.com/fzltech/fmverify/issues/36) — `Fmverify.Annotations` attributes need
  `AllowMultiple = true`; the documented multiple-`[Ensures]` form does not compile.
- [#37](https://github.com/fzltech/fmverify/issues/37) — the F5 checker accepts nonlinear
  multiplication but Z3 does not terminate; add a timeout and/or reject the construct.
- [#38](https://github.com/fzltech/fmverify/issues/38) — the source-copy annotation file fails
  strict target analyzers (`CS1591`, `IDE0073`).
- [#39](https://github.com/fzltech/fmverify/issues/39) — the bridge coverage line reports “no
  annotations” when the target has annotations but no registered conformance check.

All five landed in fmverify, and this branch was refreshed against them on 2026-11-07: the layer
is at annotations `2026.09.2` and package `0.3.3`, the contracts are split, and the bridge coverage
line now says no registered check reported coverage instead of claiming the target declares none.

## What this does and does not say

The four claims are about the arithmetic seams, not about the whole toolkit. They say the slice
pitch, column offset, last-index calculation, and non-negative batch stop are exact and
overflow-free inside the stated bounds. They do not say `Memory2D` is memory-safe for every size,
and they do not say `ParallelHelper.For` is correct for every range: the full-range count fix is
covered by conformance tests, not by the symbolic engine.

The model in `verification/src/CommunityToolkit.HighPerformance.Spec` is still the seed scalar
model generated by `init`. The gate is green because the seed property is true, not because the
system has been modelled. The useful evidence in this layer is the `SymbolicMethodChecks` gate and
the regression tests, and the case study says so rather than letting a green gate imply more.
