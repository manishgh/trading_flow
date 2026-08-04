namespace TradingFlow.Mobile.Services;

/// <summary>
/// The tactile signals the app is allowed to emit.
/// </summary>
/// <remarks>
/// Deliberately small. Android's haptic guidance is that a handful of reusable
/// patterns should carry consistent meaning, and that over-use numbs the channel
/// until users disable it. Strength tracks importance and inverse frequency, so
/// frequent interactions - filter chips, row selection, scrolling, refresh - emit
/// nothing at all and are absent from this enum by design.
/// </remarks>
public enum HapticSignal
{
    /// <summary>A server review passed and a confirm control has become available.</summary>
    ReviewReady,

    /// <summary>An order or position exit was accepted by the broker.</summary>
    OrderAccepted,

    /// <summary>An order was rejected or blocked before submission.</summary>
    OrderRejected,

    /// <summary>A new actionable strategy signal arrived while the app was in front.</summary>
    SignalArrived,

    /// <summary>The operator is about to commit something irreversible.</summary>
    DestructiveConfirm
}

/// <summary>
/// Emits haptic feedback for state changes the operator caused or must act on.
/// </summary>
public interface IHapticSignalService
{
    /// <summary>Whether the operator has haptics switched on. Defaults to on.</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Emits <paramref name="signal"/>. Never throws: a device without a vibrator,
    /// a revoked permission, or a disabled system setting degrades to silence.
    /// Haptics are always supplementary, never the only feedback for an event.
    /// </summary>
    Task SignalAsync(HapticSignal signal);
}

/// <inheritdoc />
public sealed class HapticSignalService : IHapticSignalService
{
    private const string EnabledPreferenceKey = "haptics.enabled";

    /// <summary>
    /// Gap between pulses in a multi-pulse pattern. Long enough to read as two
    /// distinct taps rather than one buzz, short enough to feel like one event.
    /// </summary>
    private static readonly TimeSpan PulseGap = TimeSpan.FromMilliseconds(90);

    public bool IsEnabled
    {
        get => Preferences.Default.Get(EnabledPreferenceKey, true);
        set => Preferences.Default.Set(EnabledPreferenceKey, value);
    }

    public async Task SignalAsync(HapticSignal signal)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            switch (signal)
            {
                // Highest-stakes success. Two pulses so it cannot be mistaken for
                // the single pulse that merely means "review passed".
                case HapticSignal.OrderAccepted:
                    await PulseAsync(HapticFeedbackType.Click, 2);
                    break;

                // Sharper and longer than success, matching the guidance that a
                // stronger pulse should warn of risk.
                case HapticSignal.OrderRejected:
                    await PulseAsync(HapticFeedbackType.LongPress, 2);
                    break;

                // Warns before an irreversible act.
                case HapticSignal.DestructiveConfirm:
                    Perform(HapticFeedbackType.LongPress);
                    break;

                // Ordinary state confirmation.
                case HapticSignal.ReviewReady:
                case HapticSignal.SignalArrived:
                    Perform(HapticFeedbackType.Click);
                    break;
            }
        }
        catch (FeatureNotSupportedException)
        {
            // No vibrator on this device.
        }
        catch (PermissionException)
        {
            // VIBRATE not granted.
        }
    }

    private static async Task PulseAsync(HapticFeedbackType type, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                await Task.Delay(PulseGap);
            }

            Perform(type);
        }
    }

    private static void Perform(HapticFeedbackType type) => HapticFeedback.Default.Perform(type);
}
