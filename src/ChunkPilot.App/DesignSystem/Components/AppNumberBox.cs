using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// Bounded integer entry with stepper buttons.
/// </summary>
/// <remarks>
/// Values are clamped to <see cref="Minimum"/> and <see cref="Maximum"/> on every path, including
/// direct typing, so a port or retention value cannot leave its valid range through the UI.
/// Replaces the earlier <c>NumericUpDown</c>, which carried its own hard-coded surface colour.
/// </remarks>
public sealed class AppNumberBox : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(AppNumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(AppNumberBox), new PropertyMetadata(0, OnBoundsChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(AppNumberBox), new PropertyMetadata(int.MaxValue, OnBoundsChanged));

    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
        nameof(Increment), typeof(int), typeof(AppNumberBox), new PropertyMetadata(1));

    public AppNumberBox()
    {
        IncreaseCommand = new RelayCommand(() => Value = Clamp(Value + Math.Max(1, Increment), Minimum, Maximum));
        DecreaseCommand = new RelayCommand(() => Value = Clamp(Value - Math.Max(1, Increment), Minimum, Maximum));
    }

    /// <summary>The current value, always within bounds.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Clamp(value, Minimum, Maximum));
    }

    /// <summary>Inclusive lower bound.</summary>
    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Inclusive upper bound.</summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Step applied by the increase and decrease commands.</summary>
    public int Increment
    {
        get => (int)GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    /// <summary>Bound by the shared template to the increase button.</summary>
    public ICommand IncreaseCommand { get; }

    /// <summary>Bound by the shared template to the decrease button.</summary>
    public ICommand DecreaseCommand { get; }

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var control = (AppNumberBox)target;
        var clamped = Clamp((int)args.NewValue, control.Minimum, control.Maximum);
        if (clamped != (int)args.NewValue)
            control.SetCurrentValue(ValueProperty, clamped);
    }

    private static void OnBoundsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var control = (AppNumberBox)target;
        control.SetCurrentValue(ValueProperty, Clamp(control.Value, control.Minimum, control.Maximum));
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        minimum <= maximum ? Math.Clamp(value, minimum, maximum) : value;
}
