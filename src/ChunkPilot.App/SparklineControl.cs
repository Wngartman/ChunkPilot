using System.Globalization;
using System.Windows.Automation;
using System.Windows.Media;
using ChunkPilot.Core;

namespace ChunkPilot.App;

/// <summary>A bounded, lightweight chart for real server-process samples.</summary>
public sealed class SparklineControl : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<StatisticsSample>), typeof(SparklineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnInputChanged));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(string), typeof(SparklineControl),
        new FrameworkPropertyMetadata("Cpu", FrameworkPropertyMetadataOptions.AffectsRender, OnInputChanged));

    public static readonly DependencyProperty MemoryLimitMbProperty = DependencyProperty.Register(
        nameof(MemoryLimitMb), typeof(int), typeof(SparklineControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnInputChanged));

    private static readonly DependencyPropertyKey CurrentTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(CurrentText), typeof(string), typeof(SparklineControl),
            new PropertyMetadata(""));
    private static readonly DependencyPropertyKey AverageTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(AverageText), typeof(string), typeof(SparklineControl),
            new PropertyMetadata(""));
    private static readonly DependencyPropertyKey PeakTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(PeakText), typeof(string), typeof(SparklineControl),
            new PropertyMetadata(""));
    private static readonly DependencyPropertyKey WindowTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(WindowText), typeof(string), typeof(SparklineControl),
            new PropertyMetadata(""));
    private static readonly DependencyPropertyKey ScaleTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(ScaleText), typeof(string), typeof(SparklineControl),
            new PropertyMetadata(""));

    public static readonly DependencyProperty CurrentTextProperty = CurrentTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty AverageTextProperty = AverageTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty PeakTextProperty = PeakTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty WindowTextProperty = WindowTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ScaleTextProperty = ScaleTextPropertyKey.DependencyProperty;

    private double[] renderedValues = [];
    private double axisMaximum = 1;

    public IReadOnlyList<StatisticsSample>? Samples
    {
        get => (IReadOnlyList<StatisticsSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public string Metric
    {
        get => (string)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public int MemoryLimitMb
    {
        get => (int)GetValue(MemoryLimitMbProperty);
        set => SetValue(MemoryLimitMbProperty, value);
    }

    public string CurrentText => (string)GetValue(CurrentTextProperty);
    public string AverageText => (string)GetValue(AverageTextProperty);
    public string PeakText => (string)GetValue(PeakTextProperty);
    public string WindowText => (string)GetValue(WindowTextProperty);
    public string ScaleText => (string)GetValue(ScaleTextProperty);

    /// <summary>Calculates the truthful text and scale used by rendering and accessibility.</summary>
    public static PerformanceMetricSummary Summarize(
        IReadOnlyList<StatisticsSample>? samples,
        string metric,
        int memoryLimitMb = 0)
    {
        var isMemory = metric.Equals("Ram", StringComparison.OrdinalIgnoreCase);
        var values = samples?.Select(sample =>
                isMemory ? (double)Math.Max(0, sample.WorkingSetBytes) : Math.Max(0, sample.CpuPercent))
            .ToArray() ?? [];
        if (values.Length == 0)
            return PerformanceMetricSummary.Empty(isMemory ? "Memory" : "CPU");

        var current = values[^1];
        var average = values.Average();
        var peak = values.Max();
        var configuredMaximum = isMemory && memoryLimitMb > 0
            ? memoryLimitMb * 1024d * 1024d
            : 0;
        var axis = isMemory ? Math.Max(1, Math.Max(configuredMaximum, peak)) : 100d;
        var currentText = isMemory ? FormatBytes(current) : FormatPercent(current);
        var averageText = isMemory ? FormatBytes(average) : FormatPercent(average);
        var peakText = isMemory ? FormatBytes(peak) : FormatPercent(peak);
        var windowText = DescribeWindow(samples!);
        var scaleText = isMemory
            ? configuredMaximum > 0 && peak <= configuredMaximum
                ? $"Scale 0–{FormatBytes(configuredMaximum)} configured"
                : $"Scale 0–{FormatBytes(axis)} observed"
            : "Scale 0–100%";
        var name = isMemory ? "Memory" : "CPU";
        return new PerformanceMetricSummary(
            name, values, axis, currentText, averageText, peakText, windowText, scaleText,
            $"{name}. Current {currentText}. Average {averageText}. Peak {peakText}. {windowText}. {scaleText}.");
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (renderedValues.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var baseline = ResolveBrush("AppStrokeSubtle");
        var line = ResolveBrush(Metric.Equals("Ram", StringComparison.OrdinalIgnoreCase)
            ? "AppInfo"
            : "AppAccent");
        var gridPen = new Pen(baseline, 1);
        for (var reference = 0; reference <= 4; reference++)
        {
            var y = reference * Math.Max(0, ActualHeight - 1) / 4d;
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < renderedValues.Length; index++)
            {
                var point = new Point(
                    index * ActualWidth / Math.Max(1, renderedValues.Length - 1),
                    ActualHeight - Math.Clamp(renderedValues[index] / axisMaximum, 0, 1) *
                    Math.Max(0, ActualHeight - 2) - 1);
                if (index == 0)
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                else
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(line, 2), geometry);
    }

    private static void OnInputChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var chart = (SparklineControl)dependencyObject;
        var summary = Summarize(chart.Samples, chart.Metric, chart.MemoryLimitMb);
        chart.renderedValues = summary.Values;
        chart.axisMaximum = summary.AxisMaximum;
        chart.SetValue(CurrentTextPropertyKey, summary.CurrentText);
        chart.SetValue(AverageTextPropertyKey, summary.AverageText);
        chart.SetValue(PeakTextPropertyKey, summary.PeakText);
        chart.SetValue(WindowTextPropertyKey, summary.WindowText);
        chart.SetValue(ScaleTextPropertyKey, summary.ScaleText);
        chart.ToolTip = summary.HasData ? summary.AccessibleText : null;
        AutomationProperties.SetHelpText(chart, summary.AccessibleText);
        chart.InvalidateVisual();
    }

    private static string DescribeWindow(IReadOnlyList<StatisticsSample> samples)
    {
        if (samples.Count == 1)
            return "1 real sample";
        var duration = samples[^1].Timestamp - samples[0].Timestamp;
        var elapsed = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        var durationText = elapsed.TotalMinutes >= 1
            ? string.Create(CultureInfo.CurrentCulture,
                $"{Math.Floor(elapsed.TotalMinutes):0} min {elapsed.Seconds:0} sec")
            : string.Create(CultureInfo.CurrentCulture, $"{elapsed.TotalSeconds:0} sec");
        return $"{samples.Count} real samples over {durationText}";
    }

    private static string FormatPercent(double value) =>
        string.Create(CultureInfo.CurrentCulture, $"{value:F1}%");

    private static string FormatBytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.CurrentCulture, $"{value:F1} {units[unit]}");
    }

    private Brush ResolveBrush(string tokenKey) =>
        TryFindResource(tokenKey) as Brush ??
        Application.Current?.TryFindResource(tokenKey) as Brush ??
        Brushes.Transparent;
}

public sealed record PerformanceMetricSummary(
    string Name,
    double[] Values,
    double AxisMaximum,
    string CurrentText,
    string AverageText,
    string PeakText,
    string WindowText,
    string ScaleText,
    string AccessibleText)
{
    public bool HasData => Values.Length > 0;

    public static PerformanceMetricSummary Empty(string name) =>
        new(name, [], 1, "", "", "", "No real samples", "", $"{name}. No real samples.");
}
