using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoConverterUI.Controls;

/// <summary>
/// A Decorator WPF doesn't clip to its layout slot when it is bigger than the slot. A drop-down's rows sit in one while
/// the glass around them morphs (DropdownAnimBehavior): they keep their full size and the glass panel's own clip cuts
/// them, whatever the scale they are at.
/// </summary>
public sealed class UnclippedDecorator : Decorator
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;
}
