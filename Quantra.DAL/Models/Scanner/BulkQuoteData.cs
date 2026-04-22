using System;
using System.Collections.Generic;

namespace Quantra.Models.Scanner
{
    /// <summary>
    /// Single symbol snapshot returned by Alpha Vantage REALTIME_BULK_QUOTES endpoint.
    /// Up to 100 symbols may be returned per request.
    /// </summary>
    public class BulkQuoteData
    {
        public string Symbol { get; set; }
        public DateTime Timestamp { get; set; }

        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }

        public double PreviousClose { get; set; }
        public double Change { get; set; }
        public double ChangePercent { get; set; }

        public double? ExtendedHoursQuote { get; set; }
        public double? ExtendedHoursChange { get; set; }
        public double? ExtendedHoursChangePercent { get; set; }

        public double GapPercent
        {
            get
            {
                if (PreviousClose <= 0) return 0;
                return (Open - PreviousClose) / PreviousClose * 100.0;
            }
        }

        public double ApproxVwap => (High + Low + Close) / 3.0;

        public double VwapDeviationPercent
        {
            get
            {
                var v = ApproxVwap;
                if (v <= 0) return 0;
                return (Close - v) / v * 100.0;
            }
        }

        public double DayRangePercent
        {
            get
            {
                if (Close <= 0) return 0;
                return (High - Low) / Close * 100.0;
            }
        }
    }

    public class BulkQuotesResponse
    {
        public string Endpoint { get; set; }
        public string Message { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<BulkQuoteData> Quotes { get; set; } = new List<BulkQuoteData>();
    }
}
