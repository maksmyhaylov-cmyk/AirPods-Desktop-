using Windows.Media.Control;

namespace AirPodsWin;

/// <summary>Pauses/resumes whatever is playing (Spotify, browser, players) via Windows media sessions.</summary>
public static class MediaControl
{
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    private static async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _manager.GetCurrentSession();
    }

    /// <summary>Pauses playback; returns true if something was actually playing.</summary>
    public static async Task<bool> PauseIfPlayingAsync()
    {
        var session = await GetSessionAsync();
        if (session == null) return false;
        var status = session.GetPlaybackInfo().PlaybackStatus;
        if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return false;
        return await session.TryPauseAsync();
    }

    public static async Task PlayAsync()
    {
        var session = await GetSessionAsync();
        if (session != null) await session.TryPlayAsync();
    }
}
