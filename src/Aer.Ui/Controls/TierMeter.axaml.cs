using Avalonia;
using Avalonia.Controls;

namespace Aer.Ui.Controls;

/// <summary>
/// The depth/effort meter control (#1318) — see <c>TierMeter.axaml</c>'s own header for the design
/// reasoning. <see cref="Steps"/> null or empty renders nothing: the chip's absence rule for a null,
/// raw, or unmapped tier (decision 0058's scope ruling 2) is enforced by the converters that build
/// this list, not by this control, which only draws whatever it is handed.
/// </summary>
public sealed partial class TierMeter : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<TierMeterStep>?> StepsProperty =
        AvaloniaProperty.Register<TierMeter, IReadOnlyList<TierMeterStep>?>(nameof(Steps));

    public IReadOnlyList<TierMeterStep>? Steps
    {
        get => GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    public TierMeter()
    {
        InitializeComponent();
    }
}
