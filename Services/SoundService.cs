using System;
using System.IO;
using System.Reflection;
using System.Windows.Resources;
using System.Windows;
using System.Media;

namespace ColorKids.Services;

/// <summary>Plays embedded .wav sound effects.</summary>
public class SoundService
{
    private readonly SoundPlayer _brush = LoadPlayer("Resources/Sounds/brush.wav");
    private readonly SoundPlayer _fill  = LoadPlayer("Resources/Sounds/fill.wav");
    private readonly SoundPlayer _clear = LoadPlayer("Resources/Sounds/clear.wav");

    public void PlayBrush() => PlaySafe(_brush);
    public void PlayFill()  => PlaySafe(_fill);
    public void PlayClear() => PlaySafe(_clear);

    private static void PlaySafe(SoundPlayer player)
    {
        try { player.Play(); }
        catch { /* sound is optional, never crash */ }
    }

    private static SoundPlayer LoadPlayer(string resourcePath)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute);
            StreamResourceInfo sri = Application.GetResourceStream(uri);
            if (sri != null)
                return new SoundPlayer(sri.Stream);
        }
        catch { /* resource may not exist yet */ }
        return new SoundPlayer();
    }
}
