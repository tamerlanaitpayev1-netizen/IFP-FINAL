using Microsoft.JSInterop;

namespace ArenaQuiz.Client.Services;

/// <summary>
/// Browser WebAudio sound feedback synthesizer with mute toggle.
/// </summary>
public sealed class SoundService(IJSRuntime js)
{
    public bool IsMuted { get; private set; }

    public void ToggleMute() => IsMuted = !IsMuted;

    public async Task PlayAsync(string cue)
    {
        if (IsMuted)
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync("arenaAudio.playTone", cue);
        }
        catch
        {
            // Ignore audio errors when browser blocks autoplay or during teardown.
        }
    }
}

