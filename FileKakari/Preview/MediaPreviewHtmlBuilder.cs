using System;
using System.IO;
using System.Net;

namespace FileKakari;

public sealed record MediaPreviewDocument(
    string Html,
    string MediaType,
    bool EffectiveAutoPlay,
    bool EffectiveMuted);

public static class MediaPreviewHtmlBuilder
{
    public static MediaPreviewDocument Build(
        string mediaPath,
        bool isVideo,
        bool isAudio,
        bool autoPlayVideoSetting,
        bool muteVideoSetting,
        bool autoPlayAudioSetting)
    {
        var autoPlay = isVideo ? autoPlayVideoSetting : autoPlayAudioSetting;
        var muted = isVideo && autoPlay && muteVideoSetting;
        var mediaType = isVideo ? "video" : isAudio ? "audio" : "media";
        var autoplayAttr = autoPlay ? "autoplay" : "";
        var mutedAttr = muted ? "muted" : "";
        var mediaFileUri = WebUtility.HtmlEncode(new Uri(Path.GetFullPath(mediaPath)).AbsoluteUri);
        var mediaTag = isVideo
            ? $"<video id=\"media\" controls {autoplayAttr} {mutedAttr} src=\"{mediaFileUri}\"></video>"
            : $"<audio id=\"media\" controls {autoplayAttr} {mutedAttr} src=\"{mediaFileUri}\"></audio>";
        var autoplayJs = autoPlay ? "true" : "false";
        var mutedJs = muted ? "true" : "false";

        var html = $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        body {{
            margin: 0;
            padding: 0;
            background: #000;
            overflow: hidden;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            width: 100vw;
        }}
        video, audio {{
            max-width: 100%;
            max-height: 100%;
            outline: none;
        }}
        audio {{
            width: 80%;
        }}
    </style>
</head>
<body>
    {mediaTag}
    <script>
        window.playError = '';
        window.addEventListener('DOMContentLoaded', () => {{
            var media = document.getElementById('media');
            if (media) {{
                media.muted = {mutedJs};
                if ({autoplayJs}) {{
                    media.play().catch(err => {{
                        window.playError = err ? (err.name || err.message || 'UnknownError') : 'UnknownError';
                        console.log('Autoplay blocked or failed:', err);
                        media.pause();
                    }});
                }}
            }}
        }});
    </script>
</body>
</html>";

        return new MediaPreviewDocument(html, mediaType, autoPlay, muted);
    }
}
