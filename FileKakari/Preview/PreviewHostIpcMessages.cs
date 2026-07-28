using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileKakari.Preview;

public static class PreviewHostIpcProtocol
{
    public const int Version = 1;
    public const int MaxMessageLength = 65536; // 64KB safety limit for JSON lines
}

public class IpcMessageBase
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";
}

// ==========================================
// FileKakari -> PreviewHost (Commands)
// ==========================================

public sealed class InitializeCommand : IpcMessageBase
{
    public InitializeCommand() { Type = "Initialize"; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = PreviewHostIpcProtocol.Version;

    [JsonPropertyName("parentProcessId")]
    public int ParentProcessId { get; set; }

    [JsonPropertyName("sessionToken")]
    public string SessionToken { get; set; } = "";
}

public sealed class AttachCommand : IpcMessageBase
{
    public AttachCommand() { Type = "Attach"; }

    [JsonPropertyName("parentHwnd")]
    public long ParentHwnd { get; set; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("widthPx")]
    public int WidthPx { get; set; }

    [JsonPropertyName("heightPx")]
    public int HeightPx { get; set; }

    [JsonPropertyName("dpiX")]
    public double DpiX { get; set; } = 1.0;

    [JsonPropertyName("dpiY")]
    public double DpiY { get; set; } = 1.0;
}

public sealed class ResizeCommand : IpcMessageBase
{
    public ResizeCommand() { Type = "Resize"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("widthPx")]
    public int WidthPx { get; set; }

    [JsonPropertyName("heightPx")]
    public int HeightPx { get; set; }

    [JsonPropertyName("dpiX")]
    public double DpiX { get; set; } = 1.0;

    [JsonPropertyName("dpiY")]
    public double DpiY { get; set; } = 1.0;
}

public sealed class ShowCommand : IpcMessageBase
{
    public ShowCommand() { Type = "Show"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class HideCommand : IpcMessageBase
{
    public HideCommand() { Type = "Hide"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class SetFocusCommand : IpcMessageBase
{
    public SetFocusCommand() { Type = "SetFocus"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class PingCommand : IpcMessageBase
{
    public PingCommand() { Type = "Ping"; }
}

public sealed class ShutdownCommand : IpcMessageBase
{
    public ShutdownCommand() { Type = "Shutdown"; }
}

// ==========================================
// PreviewHost -> FileKakari (Events/Responses)
// ==========================================

public sealed class ReadyEvent : IpcMessageBase
{
    public ReadyEvent() { Type = "Ready"; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("hostProcessId")]
    public int HostProcessId { get; set; }
}

public sealed class AttachedEvent : IpcMessageBase
{
    public AttachedEvent() { Type = "Attached"; }

    [JsonPropertyName("childHwnd")]
    public long ChildHwnd { get; set; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class ResizedEvent : IpcMessageBase
{
    public ResizedEvent() { Type = "Resized"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";

    [JsonPropertyName("widthPx")]
    public int WidthPx { get; set; }

    [JsonPropertyName("heightPx")]
    public int HeightPx { get; set; }
}

public sealed class ShownEvent : IpcMessageBase
{
    public ShownEvent() { Type = "Shown"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class HiddenEvent : IpcMessageBase
{
    public HiddenEvent() { Type = "Hidden"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class FocusedEvent : IpcMessageBase
{
    public FocusedEvent() { Type = "Focused"; }

    [JsonPropertyName("paneId")]
    public string PaneId { get; set; } = "";
}

public sealed class PongEvent : IpcMessageBase
{
    public PongEvent() { Type = "Pong"; }
}

public sealed class ProcessErrorEvent : IpcMessageBase
{
    public ProcessErrorEvent() { Type = "ProcessError"; }

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = "";
}
