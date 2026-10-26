using CSweet.UI.Localization;
using CSweet.UI.Services;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class AppLocalizerTests
{
    [Fact]
    public void OptionalStatusLookupDoesNotReportAMissingTranslation()
    {
        var logger = new RecordingLogger();
        var localizer = CreateLocalizer(logger);

        Assert.False(localizer.Has("ArtifactWorkspace.Status.Approved"));
        Assert.Empty(logger.Entries);

        // A probe must not suppress a subsequent diagnostic from an actual lookup.
        Assert.Equal("ArtifactWorkspace.Status.Approved", localizer["ArtifactWorkspace.Status.Approved"]);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void MissingTranslationsWarnOnceAndKeepTheirFallbacks()
    {
        var logger = new RecordingLogger();
        var localizer = CreateLocalizer(logger);
        const string key = "Missing.Test.Translation";

        Assert.Equal(key, localizer[key]);
        Assert.Equal(key, localizer.Format(key, new { count = 2 }));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(key, entry.Message);
    }

    private static AppLocalizer CreateLocalizer(RecordingLogger logger) =>
        new(new LocalizationResources(), new AppCulture(null, new AuthSessionStore()), logger);

    private sealed class RecordingLogger : ILogger<AppLocalizer>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
