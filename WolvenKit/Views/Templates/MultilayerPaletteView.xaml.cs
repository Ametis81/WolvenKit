using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Splat;
using WolvenKit.App.Helpers;
using WolvenKit.App.ViewModels.Shell;
using WolvenKit.RED4.Types;

namespace WolvenKit.Views.Editors
{
    /// <summary>
    /// The colours a layer of a multilayer setup can take: every colorScale override of the layer's template as a
    /// swatch, grouped and ordered like the palette of the Blender add-on (see <see cref="MultilayerPalette"/>).
    /// Clicking a swatch sets the layer's colorScale; the current one is outlined.
    /// </summary>
    public partial class MultilayerPaletteView : UserControl
    {
        private const double SwatchSize = 14;

        /// <summary>Swatches of the grid: fewer per line, so larger.</summary>
        private const double GridSwatchSize = 22;

        /// <summary>Space between the blocks of two groups.</summary>
        private const double GroupGap = 8;

        /// <summary>Open or closed for all layers and files of the session.</summary>
        private static bool s_isExpanded = true;

        /// <summary>Groups as columns (grid) or as rows, for all layers and files of the session.</summary>
        private static bool s_gridMode;

        private ChunkViewModel _subscribed;

        public MultilayerPaletteView()
        {
            InitializeComponent();

            PaletteExpander.SetCurrentValue(Expander.IsExpandedProperty, s_isExpanded);
            PaletteExpander.Expanded += (_, _) => s_isExpanded = true;
            PaletteExpander.Collapsed += (_, _) => s_isExpanded = false;

            (s_gridMode ? GridModeButton : RowsModeButton).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            GridModeButton.Checked += (_, _) => SetMode(true);
            RowsModeButton.Checked += (_, _) => SetMode(false);

            DataContextChanged += (_, _) => Subscribe();
            Loaded += (_, _) => Subscribe();
            Unloaded += (_, _) => Unsubscribe();
        }

        private ChunkViewModel Cvm => DataContext as ChunkViewModel;

        private void Subscribe()
        {
            Unsubscribe();
            if (Cvm is { } cvm)
            {
                cvm.PropertyChanged += OnCvmPropertyChanged;
                _subscribed = cvm;
            }

            Rebuild();
        }

        private void Unsubscribe()
        {
            if (_subscribed is not null)
            {
                _subscribed.PropertyChanged -= OnCvmPropertyChanged;
                _subscribed = null;
            }
        }

        // the outline follows a colorScale picked in the dropdown or typed
        private void OnCvmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChunkViewModel.Data))
            {
                Rebuild();
            }
        }

        private void SetMode(bool grid)
        {
            if (s_gridMode != grid)
            {
                s_gridMode = grid;
                Rebuild();
            }
        }

        private void Rebuild()
        {
            RowsPanel.Children.Clear();
            ColumnsPanel.Children.Clear();
            GridScroll.SetCurrentValue(VisibilityProperty, s_gridMode ? Visibility.Visible : Visibility.Collapsed);
            RowsScroll.SetCurrentValue(VisibilityProperty, s_gridMode ? Visibility.Collapsed : Visibility.Visible);

            if (Cvm is not { ParentData: Multilayer_Layer layer } cvm ||
                layer.Material.DepotPath.GetResolvedText() is not { Length: > 0 } template ||
                Locator.Current.GetService<DocumentTools>() is not { } documentTools)
            {
                SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
                return;
            }

            SetCurrentValue(VisibilityProperty, Visibility.Visible);

            // no colours because there's no template: say so, instead of an empty palette
            var exists = documentTools.MultilayerTemplateExists(template);
            NotFoundText.SetCurrentValue(TextBlock.TextProperty, exists ? "" : $"Template not found in the project or the game: {template}");
            NotFoundText.SetCurrentValue(VisibilityProperty, exists ? Visibility.Collapsed : Visibility.Visible);

            CopyTemplateButton.SetCurrentValue(VisibilityProperty,
                cvm.CanCopyMultilayerTemplate() ? Visibility.Visible : Visibility.Collapsed);

            if (documentTools.GetMultilayerPalette(template) is not { Count: > 0 } colors)
            {
                PaletteExpander.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
                return;
            }

            PaletteExpander.SetCurrentValue(VisibilityProperty, Visibility.Visible);
            var current = cvm.ResolvedData is CName name ? name.GetResolvedText() : null;
            var rows = MultilayerPalette.GroupIntoRows(colors);
            PaletteExpander.SetCurrentValue(HeaderedContentControl.HeaderProperty,
                $"Palette: {colors.Count} colours in {rows.Count} groups");

            if (s_gridMode)
            {
                BuildGrid(rows, current);
                return;
            }

            // same width for every group, so the blocks line up in columns
            var groupWidth = rows.Max(row => row.Count) * (SwatchSize + 2);
            foreach (var row in rows)
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Width = groupWidth,
                    Margin = new Thickness(0, 0, GroupGap, GroupGap / 2),
                };
                foreach (var color in row)
                {
                    panel.Children.Add(CreateSwatch(color, color.Name == current));
                }

                RowsPanel.Children.Add(panel);
            }
        }

        /// <summary>
        /// The groups side by side as columns, base colour on top and the variants below it; a group shorter than
        /// the others (a custom template) simply ends earlier. Scrolled so the current colour is in view.
        /// </summary>
        private void BuildGrid(IReadOnlyList<IReadOnlyList<MultilayerPaletteColor>> groups, string current)
        {
            var currentColumn = -1;
            for (var i = 0; i < groups.Count; i++)
            {
                var column = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, 2, 0),
                };
                foreach (var color in groups[i])
                {
                    var isCurrent = color.Name == current;
                    if (isCurrent)
                    {
                        currentColumn = i;
                    }

                    column.Children.Add(CreateSwatch(color, isCurrent, GridSwatchSize));
                }

                ColumnsPanel.Children.Add(column);
            }

            var offset = currentColumn < 0 ? 0 : currentColumn * (GridSwatchSize + 4);
            Dispatcher.InvokeAsync(() =>
                GridScroll.ScrollToHorizontalOffset(Math.Max(0, offset - (GridScroll.ViewportWidth / 2))),
                DispatcherPriority.Loaded);
        }

        // Shift + wheel scrolls the grid sideways; the plain wheel keeps scrolling the panel around it
        private void GridScroll_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                GridScroll.ScrollToHorizontalOffset(GridScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private Border CreateSwatch(MultilayerPaletteColor color, bool isCurrent, double size = SwatchSize)
        {
            var brush = new SolidColorBrush(MultilayerPalette.ToColor(color));
            brush.Freeze();

            var swatch = new Border
            {
                Width = size,
                Height = size,
                Margin = new Thickness(1),
                Background = brush,
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true,
                Tag = color.Name,
                ToolTip = string.Format(CultureInfo.CurrentCulture, "{0}\n{1:0.###}  {2:0.###}  {3:0.###}", color.Name, color.R, color.G, color.B),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, isCurrent ? "ContentForeground" : "BorderAlt");
            swatch.MouseLeftButtonUp += Swatch_OnMouseLeftButtonUp;
            return swatch;
        }

        private async void CopyTemplateButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (Cvm is { } cvm)
            {
                await cvm.CopyMultilayerTemplateToProjectCommand.ExecuteAsync(null);
                Rebuild();
            }
        }

        // set as the dropdown does: the layer row, its swatch and the dropdown follow
        private void Swatch_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name } && Cvm is { } cvm)
            {
                cvm.Data = (CName)name;
                e.Handled = true;
            }
        }
    }
}
