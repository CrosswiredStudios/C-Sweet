namespace CSweet.UI.Components.FileViewers;

/// <summary>
/// Immutable <see cref="IFileContentViewer"/> descriptor. Use this to register a viewer in
/// <see cref="FileViewerRegistry"/>.
/// </summary>
public sealed record FileViewerDescriptor(
    IReadOnlyCollection<string> SupportedExtensions,
    Type ComponentType,
    bool SupportsPreview,
    string PreviewLabel = "Preview",
    string RawLabel = "Raw") : IFileContentViewer;
