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
            : $"<audio id=\"media\" controls {autoplayAttr} src=\"{mediaFileUri}\"></audio>";
        var autoplayJs = autoPlay ? "true" : "false";
        var mutedJs = muted ? "true" : "false";
        var isVideoJs = isVideo ? "true" : "false";

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
        let playAttempted = false;

        const tryPlay = async () => {{
            var media = document.getElementById('media');
            if (!media || playAttempted) {{
                return;
            }}
            if (!document.contains(media)) {{
                return;
            }}

            playAttempted = true;

            try {{
                await media.play();
                window.playError = '';
            }} catch (error) {{
                window.playError = error ? (error.name || error.message || String(error)) : 'UnknownError';
                console.log('Autoplay blocked or failed:', error);
                media.pause();
            }}
        }};

        window.addEventListener('DOMContentLoaded', () => {{
            var media = document.getElementById('media');
            if (media) {{
                if ({isVideoJs}) {{
                    media.muted = {mutedJs};
                }}
                if ({autoplayJs}) {{
                    if (media.readyState >= 2) {{
                        void tryPlay();
                    }} else {{
                        media.addEventListener('canplay', tryPlay, {{ once: true }});
                    }}
                }}
            }}
        }});
    </script>
</body>
</html>";

        return new MediaPreviewDocument(html, mediaType, autoPlay, muted);
    }
}
