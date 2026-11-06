namespace CSweet.UnitTests;

// Tests of behavior the product only offers on Windows (for example assisted local Office
// setup, which Linux deliberately refuses in favor of the signed package). They skip on other
// platforms; the Windows job in .github/workflows/ci.yml runs them, so add a new class that
// uses these attributes to that job's filter.

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows-only behavior; covered by the Windows CI job."; }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows-only behavior; covered by the Windows CI job."; }
}
