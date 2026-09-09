using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Expressions;

internal sealed class ExperienceMeter(IPluginHost host)
{
    private long _lastExperience;
    private long _lastLuminance;
    private bool _hasBaseline;

    public double DurationSeconds { get; private set; }
    public long Experience { get; private set; }
    public long Luminance { get; private set; }
    public double ExperiencePerHour => DurationSeconds > 0d
        ? Experience / DurationSeconds * 3600d
        : 0d;
    public double LuminancePerHour => DurationSeconds > 0d
        ? Luminance / DurationSeconds * 3600d
        : 0d;

    public void OnTick(double elapsedSeconds)
    {
        if (!host.Automation.Character.IsInWorld
            || !host.Automation.Objects.TryCaptureProperties(
                host.Automation.Character.ObjectId,
                out PluginItemProperties properties))
        {
            _hasBaseline = false;
            return;
        }
        long experience = properties.Int64s.TryGetValue(1u, out long xp) ? xp : 0L;
        long luminance = properties.Int64s.TryGetValue(6u, out long lum) ? lum : 0L;
        if (!_hasBaseline)
        {
            _lastExperience = experience;
            _lastLuminance = luminance;
            _hasBaseline = true;
        }
        else
        {
            if (experience >= _lastExperience)
                Experience = checked(Experience + experience - _lastExperience);
            if (luminance >= _lastLuminance)
                Luminance = checked(Luminance + luminance - _lastLuminance);
            _lastExperience = experience;
            _lastLuminance = luminance;
        }
        DurationSeconds += elapsedSeconds;
    }

    public void Reset()
    {
        DurationSeconds = 0d;
        Experience = 0L;
        Luminance = 0L;
        _hasBaseline = false;
    }

    public string Format()
    {
        string result = Experience.ToString("N0", CultureInfo.InvariantCulture)
            + " XP";
        if (Luminance != 0)
        {
            result += " and "
                + Luminance.ToString("N0", CultureInfo.InvariantCulture)
                + " LUM";
        }
        result += ", "
            + DurationSeconds.ToString("N0", CultureInfo.InvariantCulture)
            + "s, "
            + ExperiencePerHour.ToString("N0", CultureInfo.InvariantCulture)
            + " XP/hr";
        if (Luminance != 0)
        {
            result += " and "
                + LuminancePerHour.ToString("N0", CultureInfo.InvariantCulture)
                + " LUM/hr";
        }
        return result;
    }
}
