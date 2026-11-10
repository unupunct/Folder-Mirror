using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FolderMirror.Core.Models;

namespace FolderMirror.Services;

static class Brushes2
{
    public static Brush Get(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public sealed class ActionBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Brushes2.Get(value switch
    {
        PlanAction.New => "NewBrush", PlanAction.Modified => "ModifiedBrush", PlanAction.Delete => "DeleteBrush",
        PlanAction.Unchanged => "UnchangedBrush", PlanAction.Conflict => "ConflictBrush", _ => "TextDimBrush"
    });
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Brushes2.Get((value?.ToString() ?? "") switch
    {
        "Completed" => "NewBrush", "CompletedWithErrors" => "ConflictBrush", "Failed" => "DeleteBrush", "Cancelled" => "TextDimBrush",
        "Skipped" => "TextDimBrush", "Interrupted" => "ConflictBrush", "Running" => "ModifiedBrush", "OK" => "NewBrush", "Resolved" => "NewBrush",
        "Error" => "DeleteBrush", _ => "TextDimBrush"
    });
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class OpActionBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Brushes2.Get((value?.ToString() ?? "") switch
    {
        "New" => "NewBrush", "Modified" => "ModifiedBrush", "Deleted" => "DeleteBrush", "Conflict" => "ConflictBrush", "Error" => "DeleteBrush", _ => "TextDimBrush"
    });
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Brushes2.Get(value is IssueSeverity s
        ? s switch { IssueSeverity.Error => "ErrorBgBrush", IssueSeverity.Warning => "WarnBgBrush", _ => "InfoBgBrush" } : "InfoBgBrush");
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is IssueSeverity s
        ? s switch { IssueSeverity.Error => "", IssueSeverity.Warning => "", _ => "" } : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : v;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value == null || (value is string s && s.Length == 0)) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v ? v : value is int n && n > 0;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p != null ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}

public sealed class ConflictVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is PlanAction a && a == PlanAction.Conflict ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EnumTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SyncMode m => ModeInfo.Title(m),
        ConflictPolicy cp => ViewModels.WizardViewModel.ConflictText(cp),
        DeletionBehavior d => d == DeletionBehavior.MoveToArchive ? "Move to a dated archive (recommended)" : "Delete permanently",
        ScheduleKind k => ViewModels.ScheduleViewModel.KindText(k),
        ComparisonMethod cm => cm switch
        {
            ComparisonMethod.SizeAndDate => "Size and modified date", ComparisonMethod.SizeOnly => "Size only",
            ComparisonMethod.DateOnly => "Modified date only", _ => "SHA-256 content (slowest, most accurate)"
        },
        ConflictResolution r => r switch
        {
            ConflictResolution.Skip => "Leave untouched", ConflictResolution.UseSource => "Use source", ConflictResolution.UseDestination => "Use destination", _ => "Keep both"
        },
        "CompletedWithErrors" => "Completed with errors",
        null => "",
        _ => value.ToString() ?? ""
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
