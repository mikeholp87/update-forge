using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UpdateForge.Desktop.ViewModels;

namespace UpdateForge.Desktop.Views;

public partial class DashboardView : UserControl
{
    private ShellViewModel? _vm;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm is not null) _vm.Providers.CollectionChanged -= OnProvidersChanged;
        _vm = DataContext as ShellViewModel;
        if (_vm is not null) _vm.Providers.CollectionChanged += OnProvidersChanged;
        RebuildBar();
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildBar();

    /// <summary>One segment per manager, as wide as its share of the inventory. Star-sized columns give exact proportions at any window width.</summary>
    private void RebuildBar()
    {
        Bar.Children.Clear();
        Bar.ColumnDefinitions.Clear();
        var providers = _vm?.Providers;
        if (providers is null || providers.Count == 0)
        {
            Bar.Children.Add(new Border { Background = (Brush)FindResource("ControlStrokeColorDefaultBrush"), CornerRadius = new CornerRadius(4) });
            return;
        }
        var column = 0;
        foreach (var p in providers)
        {
            Bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(p.Count, 1), GridUnitType.Star) });
            var segment = new Border
            {
                Background = p.Brush,
                Margin = new Thickness(column == 0 ? 0 : 2, 0, 0, 0),   // a 2px gap separates managers
                CornerRadius = new CornerRadius(column == 0 ? 4 : 1, column == providers.Count - 1 ? 4 : 1, column == providers.Count - 1 ? 4 : 1, column == 0 ? 4 : 1),
                ToolTip = $"{p.Name}: {p.Count}",
            };
            Grid.SetColumn(segment, column++);
            Bar.Children.Add(segment);
        }
    }
}
