namespace CSweet.UI.Components.FileViewers;

/// <summary>
/// Describes how a file's content should be rendered in the source-control file panel.
/// Implementations are stateless descriptors; the actual rendering is done by the Blazor
/// component referenced by <see cref="ComponentType"/>.
/// </summary>
public interface IFileContentViewer
{
    /// <summary>
    /// Lowercase file extensions (including the leading dot, e.g. <c>".md"</c>) this viewer
    /// handles. An empty array means the viewer never matches by extension and is only usable
    /// as a fallback.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>The Blazor component that renders the viewer's primary (preview) output.</summary>
    Type ComponentType { get; }

    /// <summary>Whether this viewer offers a rendered preview distinct from the raw source.</summary>
    bool SupportsPreview { get; }

    /// <summary>Label for the preview toggle option.</summary>
    string PreviewLabel { get; }

    /// <summary>Label for the raw-source toggle option.</summary>
    string RawLabel { get; }
}
