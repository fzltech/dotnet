#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    # The solver is not on nuget.org and is not committed; fetch and hash-verify it first.
    & "$PSScriptRoot/fetch-solver.ps1"


    if (Test-Path "translation") {
        # `..`, not `.`: this script has moved into the layer, and prove must inspect the target
        # repository. Pointed at the layer it finds only the layer's own projects, cannot tell
        # which is under verification, and refuses rather than guess (I-NO-GUESS).
        if ((Get-Command fmverify -ErrorAction SilentlyContinue) -and $env:FMTRANSLATOR) {
            Write-Host "== Translation proof gate =="
            fmverify prove ..
            # $ErrorActionPreference does not stop a native command's non-zero exit.
            if ($LASTEXITCODE -ne 0) { Write-Error "Translation proof refresh failed."; exit 1 }
        }
        else {
            # The gate still checks the committed evidence; only the refresh is skipped.
            Write-Warning "translation/ present, but fmverify or FMTRANSLATOR is unavailable; checking committed evidence only."
        }
    }

    dotnet build "CommunityToolkit.HighPerformance.Verification.slnx" -c Release
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

    dotnet test "CommunityToolkit.HighPerformance.Verification.slnx" -c Release --no-build --filter "Category!=hunt"

    # After the gate, because that is when the file exists. The gate writes it before its own
    # assertion, so a red run leaves it on disk as well.
    if (Test-Path $env:FMVERIFY_COVERAGE_OUT) {
        Write-Host ""
        Write-Host "== Bridge coverage =="
        Get-Content $env:FMVERIFY_COVERAGE_OUT
    }
} finally { Pop-Location }
