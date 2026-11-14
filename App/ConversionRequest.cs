using VideoConverter.Core;

namespace VideoConverter;

/// <summary>An in-process GUI job; both frontends use the core's encoding options and tool paths.</summary>
public sealed record ConversionRequest(string Action, MediaTools Tools, string Path, string? Output = null, EncodeOptions? Options = null,
    bool Overwrite = false, FrameRange? Frames = null, ImageExportOptions? Image = null, FrameTimeline? Timeline = null);
