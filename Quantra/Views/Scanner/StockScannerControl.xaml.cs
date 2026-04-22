using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Quantra.DAL.Services;
using Quantra.Models.Scanner;

namespace Quantra.Views.Scanner
{
    /// <summary>
    /// Real-time Alpha Vantage-driven Stock Scanner control. Uses REALTIME_BULK_QUOTES for
    /// streaming price/volume and on-demand OVERVIEW + daily-bar enrichment for RVOL,
    /// Float/Short Float %, ATR %, VWAP deviation, plus pre-market volume & gap %.
    /// Warnings (halted, shell risk, dilution risk, thin volume, wide spread) are computed
    /// server-side via <see cref="StockScannerService"/>.
    /// </summary>
    public partial class StockScannerControl : UserControl
    {
        private readonly AlphaVantageService _alphaVantage;
        private readonly StockScannerService _scanner;
        private readonly LoggingService _logging;

        private readonly Dictionary<string, ScannerResult> _rowsBySymbol =
            new Dictionary<string, ScannerResult>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<ScannerResult> _allResults = new ObservableCollection<ScannerResult>();
        private readonly ICollectionView _resultsView;

        private readonly DispatcherTimer _refreshTimer;
        private CancellationTokenSource _refreshCts;
        private bool _refreshInFlight;

        /// <summary>Raised when a symbol is double-clicked in the results grid.</summary>
        public event EventHandler<string> SymbolSelected;

        public StockScannerControl()
        {
            InitializeComponent();

            try
            {
                _alphaVantage = App.ServiceProvider?.GetService(typeof(AlphaVantageService)) as AlphaVantageService;
                _scanner = App.ServiceProvider?.GetService(typeof(StockScannerService)) as StockScannerService;
                _logging = App.ServiceProvider?.GetService(typeof(LoggingService)) as LoggingService;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Service initialization error: {ex.Message}";
            }

            // Fallback construction if DI isn't wired in (e.g. design-time)
            if (_scanner == null && _alphaVantage != null)
            {
                _scanner = new StockScannerService(_alphaVantage, _logging);
            }

            _resultsView = CollectionViewSource.GetDefaultView(_allResults);
            ResultsGrid.ItemsSource = _resultsView;

            PresetComboBox.ItemsSource = ScannerPresetInfo.All;
            PresetComboBox.SelectedIndex = 0;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimer.Tick += async (s, e) => await RunRefreshAsync();

            Loaded += StockScannerControl_Loaded;
            Unloaded += StockScannerControl_Unloaded;
        }

        private async void StockScannerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_alphaVantage == null || _scanner == null)
            {
                StatusText.Text = "Scanner services not available.";
                return;
            }

            UpdateTimerFromUi();
            if (AutoRefreshCheckBox.IsChecked == true) _refreshTimer.Start();
            await RunRefreshAsync();
        }

        private void StockScannerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshCts?.Cancel();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RunRefreshAsync();

        private void AutoRefreshCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_refreshTimer == null) return;
            if (AutoRefreshCheckBox.IsChecked == true)
            {
                UpdateTimerFromUi();
                _refreshTimer.Start();
            }
            else
            {
                _refreshTimer.Stop();
            }
        }

        private void IntervalTextBox_LostFocus(object sender, RoutedEventArgs e) => UpdateTimerFromUi();

        private void UpdateTimerFromUi()
        {
            if (IntervalTextBox == null) return;
            if (int.TryParse(IntervalTextBox.Text, out var seconds) && seconds >= 5 && seconds <= 600)
            {
                _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                IntervalTextBox.Text = "30";
                _refreshTimer.Interval = TimeSpan.FromSeconds(30);
            }
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPresetFilter();
        }

        private void ApplyPresetFilter()
        {
            if (_resultsView == null) return;
            var preset = (PresetComboBox.SelectedItem as ScannerPresetInfo)?.Preset ?? ScannerPreset.None;
            _resultsView.Filter = preset == ScannerPreset.None
                ? (Predicate<object>)null
                : o => o is ScannerResult r && StockScannerService.MatchesPreset(r, preset);
            _resultsView.Refresh();
            UpdateStatusCount();
        }

        private IEnumerable<string> GetSymbolsFromInput()
        {
            var raw = SymbolsTextBox?.Text ?? string.Empty;
            return raw.Split(new[] { ',', ' ', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim().ToUpperInvariant())
                      .Where(s => s.Length > 0 && s.Length <= 12)
                      .Distinct();
        }

        private async Task RunRefreshAsync()
        {
            if (_scanner == null) return;
            if (_refreshInFlight) return; // throttle overlapping refreshes

            var symbols = GetSymbolsFromInput().ToList();
            if (symbols.Count == 0)
            {
                StatusText.Text = "Enter one or more symbols to scan.";
                return;
            }

            _refreshInFlight = true;
            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            var ct = _refreshCts.Token;

            try
            {
                LoadingIndicator.Visibility = Visibility.Visible;
                RefreshButton.IsEnabled = false;
                StatusText.Text = $"Refreshing {symbols.Count} symbol(s)...";

                var updated = await _scanner.RefreshQuotesAsync(symbols, _rowsBySymbol, ct).ConfigureAwait(true);

                // Merge into observable collection while preserving existing instances (for live updates)
                foreach (var row in updated)
                {
                    if (!_allResults.Contains(row)) _allResults.Add(row);
                }

                // Remove rows no longer in the symbol set
                var desired = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
                for (int i = _allResults.Count - 1; i >= 0; i--)
                {
                    if (!desired.Contains(_allResults[i].Symbol))
                    {
                        _rowsBySymbol.Remove(_allResults[i].Symbol);
                        _allResults.RemoveAt(i);
                    }
                }

                ApplyPresetFilter();
                StatusText.Text = $"Updated {updated.Count} symbol(s) at {DateTime.Now:HH:mm:ss}. " +
                                  $"{_allResults.Count} tracked.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Refresh cancelled.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                _logging?.LogErrorWithContext(ex, "StockScannerControl.RunRefreshAsync");
            }
            finally
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                _refreshInFlight = false;
            }
        }

        private async void EnrichButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scanner == null) return;

            EnrichButton.IsEnabled = false;
            LoadingIndicator.Visibility = Visibility.Visible;
            try
            {
                var cts = new CancellationTokenSource();
                var targets = _allResults.Where(r => !r.IsEnriched).ToList();
                if (targets.Count == 0) targets = _allResults.ToList();
                StatusText.Text = $"Enriching {targets.Count} symbol(s) with fundamentals + daily stats...";

                int done = 0;
                foreach (var row in targets)
                {
                    await _scanner.EnrichAsync(row, cts.Token).ConfigureAwait(true);
                    done++;
                    if (done % 5 == 0)
                        StatusText.Text = $"Enriched {done}/{targets.Count}...";
                }

                ApplyPresetFilter();
                StatusText.Text = $"Enrichment complete ({done} symbols).";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Enrichment error: {ex.Message}";
                _logging?.LogErrorWithContext(ex, "StockScannerControl.EnrichButton_Click");
            }
            finally
            {
                EnrichButton.IsEnabled = true;
                LoadingIndicator.Visibility = Visibility.Collapsed;
            }
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsGrid.SelectedItem is ScannerResult r && !string.IsNullOrWhiteSpace(r.Symbol))
            {
                SymbolSelected?.Invoke(this, r.Symbol);
            }
        }

        private void UpdateStatusCount()
        {
            if (_resultsView == null) return;
            int visible = _resultsView.Cast<object>().Count();
            StatusText.Text = $"{visible} of {_allResults.Count} symbol(s) match the selected preset.";
        }
    }

    #region Converters

    /// <summary>Green for positive, red for negative, gray for zero/null.</summary>
    public class ScannerSignColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double v = 0;
            if (value is double d) v = d;
            if (v > 0) return new SolidColorBrush(Color.FromRgb(80, 224, 112));
            if (v < 0) return new SolidColorBrush(Color.FromRgb(255, 107, 107));
            return new SolidColorBrush(Color.FromRgb(170, 170, 170));
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Formats nullable doubles. Parameter controls mode: "pct" => "1.23%",
    /// "signpct" => "+1.23%", "vol" => abbreviated volume, default => "1.23".
    /// </summary>
    public class NullableDoubleFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "—";
            double? d = value as double?;
            if (!d.HasValue && value is double dv) d = dv;
            if (!d.HasValue) return "—";
            var mode = parameter as string;
            switch (mode)
            {
                case "pct": return $"{d.Value:F2}%";
                case "signpct": return $"{d.Value:+0.00;-0.00}%";
                case "vol": return FormatVolume((long)d.Value);
                default: return $"{d.Value:F2}";
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        internal static string FormatVolume(long v)
        {
            if (v >= 1_000_000_000) return $"{v / 1_000_000_000.0:F2}B";
            if (v >= 1_000_000) return $"{v / 1_000_000.0:F2}M";
            if (v >= 1_000) return $"{v / 1_000.0:F1}K";
            return v.ToString("N0");
        }
    }

    public class NullableRvolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d) return $"{d:F2}x";
            return "—";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class FloatSharesFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            long? v = value as long?;
            if (!v.HasValue) return "—";
            var d = (double)v.Value;
            if (d >= 1_000_000_000) return $"{d / 1_000_000_000:F2}B";
            if (d >= 1_000_000) return $"{d / 1_000_000:F1}M";
            if (d >= 1_000) return $"{d / 1_000:F0}K";
            return v.Value.ToString("N0");
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class MarketCapFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double? v = value as double?;
            if (!v.HasValue) return "—";
            var d = v.Value;
            if (d >= 1_000_000_000_000) return $"${d / 1_000_000_000_000:F2}T";
            if (d >= 1_000_000_000) return $"${d / 1_000_000_000:F2}B";
            if (d >= 1_000_000) return $"${d / 1_000_000:F2}M";
            return $"${d:N0}";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class VolumeFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            long v = 0;
            if (value is long l) v = l;
            else if (value is int i) v = i;
            return NullableDoubleFormatConverter.FormatVolume(v);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    #endregion
}
