using System.IO;

namespace CSweet.UI.Components.FileViewers;

/// <summary>
/// Resolves which <see cref="IFileContentViewer"/> should render a given file, based on its
/// extension. This mirrors the codebase's <c>IPlatformCapabilityHandler</c> /
/// <c>PlatformCapabilityDispatcher</c> "first handler that can handle it" pattern, but is
/// UI-scoped and stateless.
///
/// To add a new viewer: create a Blazor component that accepts a <c>string Content</c>
/// parameter, then add a <see cref="FileViewerDescriptor"/> to <see cref="Viewers"/>. No other
/// code needs to change.
/// </summary>
public static class FileViewerRegistry
{
    /// <summary>
    /// The plain-text viewer used for any file that has no dedicated viewer. It never matches by
    /// extension; it is only returned by <see cref="Resolve"/> as the fallback.
    /// </summary>
    public static IFileContentViewer Fallback { get; } = new FileViewerDescriptor(
        SupportedExtensions: [],
        ComponentType: typeof(PlainTextFileViewer),
        SupportsPreview: false);

    /// <summary>All registered viewers, in priority order (first match wins).</summary>
    public static IReadOnlyList<IFileContentViewer> Viewers { get; } =
    [
        new FileViewerDescriptor(
            SupportedExtensions: [".md", ".markdown", ".mdown"],
            ComponentType: typeof(MarkdownFileViewer),
            SupportsPreview: true),
        Fallback,
    ];

    /// <summary>
    /// Returns the viewer for <paramref name="fileName"/>, or <see cref="Fallback"/> when no
    /// viewer claims the file's extension.
    /// </summary>
    public static IFileContentViewer Resolve(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return Viewers.FirstOrDefault(viewer => viewer.SupportedExtensions.Contains(extension)) ?? Fallback;
    }
}
