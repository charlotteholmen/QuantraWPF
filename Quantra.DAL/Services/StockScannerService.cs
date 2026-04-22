using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Quantra.Models;
using Quantra.Models.Scanner;

namespace Quantra.DAL.Services
{
    /// <summary>
    /// Orchestrates real-time stock scanning: bulk quotes from Alpha Vantage for price/volume
    /// plus on-demand fundamentals/daily-bar enrichment for RVOL, ATR %, Float and Short Float %.
    /// Also produces preset filters and warning diagnostics (Halted, Shell Risk, Dilution Risk,
    /// Thin Volume, Wide Spread).
    /// </summary>
    public class StockScannerService
    {
        private readonly AlphaVantageService _alphaVantage;
        private readonly LoggingService _logging;

        // Symbol-level caches for data that only needs to be refreshed occasionally.
        private readonly Dictionary<string, (CompanyOverview Overview, DateTime At)> _overviewCache
            = new Dictionary<string, (CompanyOverview, DateTime)>();
        private readonly Dictionary<string, (long AvgVol20, double? AtrPercent, DateTime At)> _dailyStatsCache
            = new Dictionary<string, (long, double?, DateTime)>();
        private readonly Dictionary<string, (bool Dilution, string Reason, DateTime At)> _dilutionCache
            = new Dictionary<string, (bool, string, DateTime)>();
        private readonly Dictionary<string, (long Volume, DateTime AsOfEt, DateTime CachedAt)> _preMarketCache
            = new Dictionary<string, (long, DateTime, DateTime)>();
        private readonly object _cacheLock = new object();

        private static readonly TimeSpan DailyStatsTtl = TimeSpan.FromHours(4);
        private static readonly TimeSpan OverviewTtl = TimeSpan.FromHours(12);
        private static readonly TimeSpan DilutionTtl = TimeSpan.FromHours(24);
        // Pre-market window is narrow and updates continuously; keep cache short.
        private static readonly TimeSpan PreMarketTtl = TimeSpan.FromMinutes(2);

        public StockScannerService(AlphaVantageService alphaVantage, LoggingService logging)
        {
            _alphaVantage = alphaVantage ?? throw new ArgumentNullException(nameof(alphaVantage));
            _logging = logging;
        }

        /// <summary>
        /// Runs one bulk-quote refresh and merges the results into the supplied dictionary of
        /// scanner rows. Returns the set of rows that were updated.
        /// </summary>
        public async Task<IReadOnlyList<ScannerResult>> RefreshQuotesAsync(
            IEnumerable<string> symbols,
            Dictionary<string, ScannerResult> rowsBySymbol,
            CancellationToken ct = default)
        {
            if (rowsBySymbol == null) throw new ArgumentNullException(nameof(rowsBySymbol));
            var updated = new List<ScannerResult>();

            var response = await _alphaVantage.GetRealtimeBulkQuotesAsync(symbols).ConfigureAwait(false);
            if (response == null || response.Quotes == null)
                return updated;

            foreach (var quote in response.Quotes)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(quote.Symbol)) continue;
                var key = quote.Symbol.ToUpperInvariant();

                if (!rowsBySymbol.TryGetValue(key, out var row))
                {
                    row = new ScannerResult { Symbol = key };
                    rowsBySymbol[key] = row;
                }

                row.Price = quote.Close;
                row.ChangePercent = quote.ChangePercent;
                row.GapPercent = quote.GapPercent;
                row.Volume = quote.Volume;
                row.VwapDeviationPercent = quote.VwapDeviationPercent;
                row.DayRangePercent = quote.DayRangePercent;
                row.PreMarketChangePercent = quote.ExtendedHoursChangePercent;
                row.LastUpdated = quote.Timestamp == default ? DateTime.UtcNow : quote.Timestamp;

                row.Warnings = BuildWarnings(row);
                updated.Add(row);
            }

            return updated;
        }

        /// <summary>
        /// Enriches a scanner row with fundamentals (OVERVIEW), daily-bar-derived stats
        /// (Avg 20-day volume, ATR %), and dilution risk from balance sheet shares trend.
        /// Caches results per-symbol to stay within API rate limits.
        /// </summary>
        public async Task EnrichAsync(ScannerResult row, CancellationToken ct = default)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Symbol)) return;
            var symbol = row.Symbol.ToUpperInvariant();

            try
            {
                var overview = await GetOverviewCachedAsync(symbol).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (overview != null)
                {
                    row.FloatShares = overview.SharesFloat ?? overview.SharesOutstanding;
                    // Alpha Vantage returns short percentages as decimal fractions
                    // (e.g. 0.0523 meaning 5.23%). Normalize to whole percent for display/filters.
                    if (overview.ShortPercentFloat.HasValue)
                        row.ShortFloatPercent = (double)overview.ShortPercentFloat.Value * 100.0;
                    else if (overview.ShortPercentOutstanding.HasValue)
                        row.ShortFloatPercent = (double)overview.ShortPercentOutstanding.Value * 100.0;
                    if (overview.MarketCapitalization.HasValue)
                        row.MarketCap = (double)overview.MarketCapitalization.Value;
                    if (!row.AverageVolume20D.HasValue && overview.AverageDailyVolume.HasValue)
                        row.AverageVolume20D = overview.AverageDailyVolume.Value;
                }

                var stats = await GetDailyStatsCachedAsync(symbol).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (stats.HasValue)
                {
                    row.AverageVolume20D = stats.Value.AvgVol20;
                    row.AtrPercent = stats.Value.AtrPercent;
                }

                var dilution = await GetDilutionCachedAsync(symbol).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var preMarket = await GetPreMarketVolumeCachedAsync(symbol).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (preMarket.HasValue)
                {
                    row.PreMarketVolume = preMarket.Value.Volume;
                }

                row.IsEnriched = true;
                row.Warnings = BuildWarnings(row, dilutionOverride: dilution);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logging?.LogErrorWithContext(ex, $"StockScannerService.EnrichAsync failed for {symbol}");
            }
        }

        private async Task<CompanyOverview> GetOverviewCachedAsync(string symbol)
        {
            lock (_cacheLock)
            {
                if (_overviewCache.TryGetValue(symbol, out var cached) &&
                    DateTime.UtcNow - cached.At < OverviewTtl)
                    return cached.Overview;
            }

            var overview = await _alphaVantage.GetCompanyOverviewAsync(symbol).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _overviewCache[symbol] = (overview, DateTime.UtcNow);
            }
            return overview;
        }

        private async Task<(long AvgVol20, double? AtrPercent)?> GetDailyStatsCachedAsync(string symbol)
        {
            lock (_cacheLock)
            {
                if (_dailyStatsCache.TryGetValue(symbol, out var cached) &&
                    DateTime.UtcNow - cached.At < DailyStatsTtl)
                    return (cached.AvgVol20, cached.AtrPercent);
            }

            try
            {
                var daily = await _alphaVantage.GetDailyData(symbol, outputSize: "compact").ConfigureAwait(false);
                if (daily == null || daily.Count == 0)
                    return null;

                var bars = daily.OrderByDescending(b => b.Date).ToList();
                var last20 = bars.Take(20).ToList();
                long avgVol = last20.Count == 0 ? 0 : (long)last20.Average(b => (double)b.Volume);

                double? atrPercent = null;
                var atrBars = bars.Take(15).OrderBy(b => b.Date).ToList();
                if (atrBars.Count >= 15)
                {
                    double trSum = 0;
                    for (int i = 1; i < atrBars.Count; i++)
                    {
                        var cur = atrBars[i];
                        var prev = atrBars[i - 1];
                        var tr = Math.Max(cur.High - cur.Low,
                                 Math.Max(Math.Abs(cur.High - prev.Close),
                                          Math.Abs(cur.Low - prev.Close)));
                        trSum += tr;
                    }
                    var atr = trSum / 14.0;
                    var lastClose = atrBars.Last().Close;
                    if (lastClose > 0)
                        atrPercent = atr / lastClose * 100.0;
                }

                lock (_cacheLock)
                {
                    _dailyStatsCache[symbol] = (avgVol, atrPercent, DateTime.UtcNow);
                }
                return (avgVol, atrPercent);
            }
            catch (Exception ex)
            {
                _logging?.LogErrorWithContext(ex, $"StockScannerService.GetDailyStatsCachedAsync failed for {symbol}");
                return null;
            }
        }

        /// <summary>
        /// Fetches and caches pre-market volume for a symbol using Alpha Vantage's
        /// extended-hours intraday time series. Short TTL (2 min) keeps data fresh during
        /// the live pre-market window.
        /// </summary>
        private async Task<(long Volume, DateTime AsOfEt)?> GetPreMarketVolumeCachedAsync(string symbol)
        {
            lock (_cacheLock)
            {
                if (_preMarketCache.TryGetValue(symbol, out var cached) &&
                    DateTime.UtcNow - cached.CachedAt < PreMarketTtl)
                {
                    return (cached.Volume, cached.AsOfEt);
                }
            }

            try
            {
                var result = await _alphaVantage.GetPreMarketVolumeAsync(symbol).ConfigureAwait(false);
                if (result.HasValue)
                {
                    lock (_cacheLock)
                    {
                        _preMarketCache[symbol] = (result.Value.Volume, result.Value.AsOfEt, DateTime.UtcNow);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                _logging?.LogErrorWithContext(ex,
                    $"StockScannerService.GetPreMarketVolumeCachedAsync failed for {symbol}");
                return null;
            }
        }

        private async Task<(bool Flag, string Reason)?> GetDilutionCachedAsync(string symbol)
        {
            lock (_cacheLock)
            {
                if (_dilutionCache.TryGetValue(symbol, out var cached) &&
                    DateTime.UtcNow - cached.At < DilutionTtl)
                    return (cached.Dilution, cached.Reason);
            }

            try
            {
                var balance = await _alphaVantage.GetBalanceSheetAsync(symbol).ConfigureAwait(false);
                bool flag = false;
                string reason = null;
                if (balance != null)
                {
                    var ordered = (balance.AnnualReports ?? new List<BalanceSheetReport>())
                        .Concat(balance.QuarterlyReports ?? new List<BalanceSheetReport>())
                        .Where(r => r.CommonStockSharesOutstanding.HasValue)
                        .OrderByDescending(r => r.FiscalDateEnding)
                        .ToList();

                    if (ordered.Count >= 2)
                    {
                        var latest = ordered[0].CommonStockSharesOutstanding.Value;
                        var prior = ordered[1].CommonStockSharesOutstanding.Value;
                        if (prior > 0)
                        {
                            var growth = (latest - prior) / prior;
                            if (growth >= 0.10m)
                            {
                                flag = true;
                                reason = $"Shares outstanding up {growth:P1} vs prior period";
                            }
                        }
                    }
                }

                lock (_cacheLock)
                {
                    _dilutionCache[symbol] = (flag, reason, DateTime.UtcNow);
                }
                return (flag, reason);
            }
            catch (Exception ex)
            {
                _logging?.LogErrorWithContext(ex, $"StockScannerService.GetDilutionCachedAsync failed for {symbol}");
                return null;
            }
        }

        /// <summary>
        /// Produces the warning list for a scanner row based on latest quote and enriched fields.
        /// </summary>
        public static List<ScannerWarning> BuildWarnings(ScannerResult row, (bool Flag, string Reason)? dilutionOverride = null)
        {
            var list = new List<ScannerWarning>();
            if (row == null) return list;

            // Halted: stale timestamp + zero volume during RTH-ish window
            var age = DateTime.UtcNow - row.LastUpdated;
            if (row.Volume == 0 && age > TimeSpan.FromMinutes(5))
            {
                list.Add(new ScannerWarning
                {
                    Type = ScannerWarningType.Halted,
                    Reason = "Zero volume with stale quote (possible halt or after-hours)"
                });
            }

            // Shell risk: sub-$50M market cap with tiny float
            if (row.MarketCap.HasValue && row.MarketCap.Value < 50_000_000 &&
                row.FloatShares.HasValue && row.FloatShares.Value < 5_000_000)
            {
                list.Add(new ScannerWarning
                {
                    Type = ScannerWarningType.ShellRisk,
                    Reason = $"Micro-cap (${row.MarketCap.Value / 1_000_000:F1}M) with {row.FloatShares.Value / 1_000_000:F2}M float"
                });
            }

            // Thin volume: < 100k average daily
            if (row.AverageVolume20D.HasValue && row.AverageVolume20D.Value > 0 &&
                row.AverageVolume20D.Value < 100_000)
            {
                list.Add(new ScannerWarning
                {
                    Type = ScannerWarningType.ThinVolume,
                    Reason = $"20-day avg volume {row.AverageVolume20D.Value:N0}"
                });
            }

            // Wide spread: day range > 8% on thin volume or mid-session (proxy for wide spread)
            if (row.DayRangePercent >= 8.0 &&
                (!row.AverageVolume20D.HasValue || row.Volume < row.AverageVolume20D.Value / 2))
            {
                list.Add(new ScannerWarning
                {
                    Type = ScannerWarningType.WideSpread,
                    Reason = $"Day range {row.DayRangePercent:F1}% with light volume"
                });
            }

            // Dilution: from balance sheet trend when supplied
            if (dilutionOverride.HasValue && dilutionOverride.Value.Flag)
            {
                list.Add(new ScannerWarning
                {
                    Type = ScannerWarningType.DilutionRisk,
                    Reason = dilutionOverride.Value.Reason ?? "Shares outstanding rising"
                });
            }

            return list;
        }

        /// <summary>
        /// Returns true if the row matches the given preset's criteria.
        /// </summary>
        public static bool MatchesPreset(ScannerResult r, ScannerPreset preset)
        {
            if (r == null) return false;
            switch (preset)
            {
                case ScannerPreset.None:
                    return true;

                case ScannerPreset.GapAndGo:
                    // Gap up >= 3% and price holding above approximate VWAP
                    return r.GapPercent >= 3.0 &&
                           r.VwapDeviationPercent >= 0 &&
                           (!r.Rvol.HasValue || r.Rvol.Value >= 1.5);

                case ScannerPreset.Breakout:
                    // Strong relative volume with positive change, above VWAP
                    return r.Rvol.HasValue && r.Rvol.Value >= 2.0 &&
                           r.ChangePercent >= 2.0 &&
                           r.VwapDeviationPercent >= 0;

                case ScannerPreset.LowFloatRunner:
                    return r.FloatShares.HasValue && r.FloatShares.Value <= 20_000_000 &&
                           r.Rvol.HasValue && r.Rvol.Value >= 3.0 &&
                           r.ChangePercent > 0;

                case ScannerPreset.ConsolidationBreak:
                    // Historically tight (low ATR %) but range/volume expanding today
                    return r.AtrPercent.HasValue && r.AtrPercent.Value <= 3.0 &&
                           r.DayRangePercent >= r.AtrPercent.Value * 1.5 &&
                           r.Rvol.HasValue && r.Rvol.Value >= 1.8;

                case ScannerPreset.ReversalWatch:
                    // Gapped down hard but now above VWAP
                    return r.GapPercent <= -2.0 && r.VwapDeviationPercent >= 0.5 &&
                           r.Rvol.HasValue && r.Rvol.Value >= 1.5;

                case ScannerPreset.PreEarningsSetup:
                    // Elevated RVOL with meaningful short interest
                    return r.Rvol.HasValue && r.Rvol.Value >= 1.5 &&
                           r.ShortFloatPercent.HasValue && r.ShortFloatPercent.Value >= 5.0;

                default:
                    return true;
            }
        }
    }
}
