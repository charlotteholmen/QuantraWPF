using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Quantra.Models.Scanner
{
    public enum ScannerPreset
    {
        None,
        GapAndGo,
        Breakout,
        LowFloatRunner,
        ConsolidationBreak,
        ReversalWatch,
        PreEarningsSetup
    }

    public enum ScannerWarningType
    {
        Halted,
        ShellRisk,
        DilutionRisk,
        ThinVolume,
        WideSpread
    }

    public class ScannerWarning
    {
        public ScannerWarningType Type { get; set; }
        public string Reason { get; set; }

        public string Label => Type switch
        {
            ScannerWarningType.Halted => "HALT",
            ScannerWarningType.ShellRisk => "SHELL",
            ScannerWarningType.DilutionRisk => "DILUT",
            ScannerWarningType.ThinVolume => "THIN",
            ScannerWarningType.WideSpread => "SPRD",
            _ => Type.ToString()
        };
    }

    public class ScannerResult : INotifyPropertyChanged
    {
        private string _symbol;
        public string Symbol
        {
            get => _symbol;
            set => SetField(ref _symbol, value);
        }

        private double _price;
        public double Price
        {
            get => _price;
            set => SetField(ref _price, value);
        }

        private double _changePercent;
        public double ChangePercent
        {
            get => _changePercent;
            set => SetField(ref _changePercent, value);
        }

        private double _gapPercent;
        public double GapPercent
        {
            get => _gapPercent;
            set => SetField(ref _gapPercent, value);
        }

        private long _volume;
        public long Volume
        {
            get => _volume;
            set
            {
                if (SetField(ref _volume, value))
                    OnPropertyChanged(nameof(Rvol));
            }
        }

        private long? _averageVolume20D;
        public long? AverageVolume20D
        {
            get => _averageVolume20D;
            set
            {
                if (SetField(ref _averageVolume20D, value))
                    OnPropertyChanged(nameof(Rvol));
            }
        }

        public double? Rvol
        {
            get
            {
                if (!AverageVolume20D.HasValue || AverageVolume20D.Value <= 0) return null;
                return (double)Volume / AverageVolume20D.Value;
            }
        }

        private double? _atrPercent;
        public double? AtrPercent
        {
            get => _atrPercent;
            set => SetField(ref _atrPercent, value);
        }

        private double _vwapDeviationPercent;
        public double VwapDeviationPercent
        {
            get => _vwapDeviationPercent;
            set => SetField(ref _vwapDeviationPercent, value);
        }

        private double? _preMarketVolume;
        public double? PreMarketVolume
        {
            get => _preMarketVolume;
            set => SetField(ref _preMarketVolume, value);
        }

        private double? _preMarketChangePercent;
        public double? PreMarketChangePercent
        {
            get => _preMarketChangePercent;
            set => SetField(ref _preMarketChangePercent, value);
        }

        private long? _floatShares;
        public long? FloatShares
        {
            get => _floatShares;
            set => SetField(ref _floatShares, value);
        }

        private double? _shortFloatPercent;
        public double? ShortFloatPercent
        {
            get => _shortFloatPercent;
            set => SetField(ref _shortFloatPercent, value);
        }

        private double? _marketCap;
        public double? MarketCap
        {
            get => _marketCap;
            set => SetField(ref _marketCap, value);
        }

        private double _dayRangePercent;
        public double DayRangePercent
        {
            get => _dayRangePercent;
            set => SetField(ref _dayRangePercent, value);
        }

        private DateTime _lastUpdated;
        public DateTime LastUpdated
        {
            get => _lastUpdated;
            set => SetField(ref _lastUpdated, value);
        }

        private bool _isEnriched;
        public bool IsEnriched
        {
            get => _isEnriched;
            set => SetField(ref _isEnriched, value);
        }

        private List<ScannerWarning> _warnings = new List<ScannerWarning>();
        public List<ScannerWarning> Warnings
        {
            get => _warnings;
            set
            {
                if (SetField(ref _warnings, value ?? new List<ScannerWarning>()))
                {
                    OnPropertyChanged(nameof(WarningsLabel));
                    OnPropertyChanged(nameof(HasWarnings));
                }
            }
        }

        public bool HasWarnings => _warnings != null && _warnings.Count > 0;

        public string WarningsLabel => _warnings == null || _warnings.Count == 0
            ? string.Empty
            : string.Join(" ", _warnings.Select(w => w.Label));

        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ScannerPresetInfo
    {
        public ScannerPreset Preset { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }

        public override string ToString() => DisplayName;

        public static IReadOnlyList<ScannerPresetInfo> All { get; } = new List<ScannerPresetInfo>
        {
            new ScannerPresetInfo { Preset = ScannerPreset.None,               DisplayName = "All Results",          Description = "No filter - show every scanned symbol." },
            new ScannerPresetInfo { Preset = ScannerPreset.GapAndGo,           DisplayName = "Gap & Go",             Description = "Gap > 3% with strong RVOL and holding above VWAP." },
            new ScannerPresetInfo { Preset = ScannerPreset.Breakout,           DisplayName = "Breakout",             Description = "Pushing through recent highs on elevated volume." },
            new ScannerPresetInfo { Preset = ScannerPreset.LowFloatRunner,     DisplayName = "Low Float Runner",     Description = "Low float (<20M) with heavy RVOL and positive change." },
            new ScannerPresetInfo { Preset = ScannerPreset.ConsolidationBreak, DisplayName = "Consolidation Break",  Description = "Tight range (low ATR%) suddenly expanding on volume." },
            new ScannerPresetInfo { Preset = ScannerPreset.ReversalWatch,      DisplayName = "Reversal Watch",       Description = "Large negative gap with upside recovery above VWAP." },
            new ScannerPresetInfo { Preset = ScannerPreset.PreEarningsSetup,   DisplayName = "Pre-earnings Setup",   Description = "Elevated volume and short float; watch for earnings runs." }
        };
    }
}
