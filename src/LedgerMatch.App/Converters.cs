using LedgerMatch.Core.Reporting;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace LedgerMatch.App;

/// <summary>Helpers used from x:Bind function bindings.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrEmpty(value);

    public static string KindName(Core.Importing.ProfileKind kind) => kind == Core.Importing.ProfileKind.Statement ? "銀行對帳單" : "收款單";

    public static double ToDouble(int value) => value;

    public static int ToInt(double value) => double.IsNaN(value) ? 0 : (int)Math.Round(value);

    public static Windows.UI.Text.FontWeight LineWeight(ReconciliationLineKind kind) =>
        kind is ReconciliationLineKind.Item or ReconciliationLineKind.Note ? FontWeights.Normal : FontWeights.SemiBold;

    public static Thickness LineIndent(ReconciliationLineKind kind) =>
        kind == ReconciliationLineKind.Item ? new Thickness(24, 0, 0, 0) : kind == ReconciliationLineKind.Heading ? new Thickness(0, 12, 0, 0) : new Thickness(0);

    public static double LineSize(ReconciliationLineKind kind) => kind == ReconciliationLineKind.Heading ? 18 : 14;
}
