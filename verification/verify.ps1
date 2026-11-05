#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    # The solver is not on nuget.org and is not committed; fetch and hash-verify it first.
    & "$PSScriptRoot/fetch-solver.ps1"


    # Build before the translation refresh: the audit warns when coverage evidence predates the
    # target assembly, and on a clean run prove writes its evidence after this line.
    dotnet build "CommunityToolkit.HighPerformance.Verification.slnx" -c Release

    if (Test-Path "translation") {
        # `..`, not `.`: this script has moved into the layer, and prove must inspect the target
        # repository. Pointed at the layer it finds only the layer's own projects, cannot tell
        # which is under verification, and refuses rather than guess (I-NO-GUESS).
        if ((Get-Command fmverify -ErrorAction SilentlyContinue) -and $env:FMTRANSLATOR) {
            # fmtranslator --backend=auto may emit .smt2 artifacts; the pinned CLI runs them.
            # An explicit Z3 path wins, so a preinstalled or locally pinned binary skips the fetch.
            if (-not $env:Z3) { & "$PSScriptRoot/fetch-z3.ps1" }
            Write-Host "== Translation proof gate =="
            fmverify prove .. --annotations=both
            # $ErrorActionPreference does not stop a native command's non-zero exit.
            if ($LASTEXITCODE -ne 0) { Write-Error "Translation proof refresh failed."; exit 1 }
        }
        else {
            # The gate still checks the committed evidence; only the refresh is skipped.
            Write-Warning "translation/ present, but fmverify or FMTRANSLATOR is unavailable; checking committed evidence only."
        }
    }

    Write-Host "== Capability honesty check =="
    # --check, not the bare JSON emit: that refuses to serialise a ConformanceBacked property,
    # because it cannot run your target and so cannot certify a claim about it -- which would take
    # this script red for a layer that had earned one. And not --emit-trust-boundary either: that
    # writes the file, and a verification step must not repair the staleness the gate exists to catch.
    dotnet run --project "src/CommunityToolkit.HighPerformance.Spec.Descriptor" -c Release --no-build -- --check
    if ($LASTEXITCODE -ne 0) { Write-Error "Descriptor reported an unproven advertised capability."; exit 1 }
    # Hunts are excluded: they draw a fresh seed every run. Run them deliberately with
    #   dotnet test "CommunityToolkit.HighPerformance.Verification.slnx" -c Release --filter Category=hunt
    # Bridge coverage -- annotations reached, model cells pinned -- is a fact about what the run
    # reached rather than a verdict, so the gate never fails over it. Which means it never prints
    # either: the gate reports failures, and a passing test's output is hidden at default verbosity.
    # Naming a path here is what makes it visible on a green run.
    $env:FMVERIFY_COVERAGE_OUT = Join-Path (Get-Location) "bridge-coverage.txt"

    # Symbolic results are published by the same run: the JSON is the audit's input, the text file
    # is the block printed below. Both are report-only, so an unwritable path never reddens the gate.
    $env:FMVERIFY_SYMBOLIC_OUT = Join-Path (Get-Location) "symbolic-coverage.json"
    $env:FMVERIFY_SYMBOLIC_TEXT_OUT = Join-Path (Get-Location) "symbolic-coverage.txt"

    dotnet test "CommunityToolkit.HighPerformance.Verification.slnx" -c Release --no-build --filter "Category!=hunt"

    # After the gate, because that is when the file exists. The gate writes it before its own
    # assertion, so a red run leaves it on disk as well.
    if (Test-Path $env:FMVERIFY_COVERAGE_OUT) {
        Write-Host ""
        Write-Host "== Bridge coverage =="
        Get-Content $env:FMVERIFY_COVERAGE_OUT
    }

    if (Test-Path $env:FMVERIFY_SYMBOLIC_TEXT_OUT) {
        Write-Host ""
        Write-Host "== Symbolic coverage =="
        Get-Content $env:FMVERIFY_SYMBOLIC_TEXT_OUT
    }
} finally { Pop-Location }
