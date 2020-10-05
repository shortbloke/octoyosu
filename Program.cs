// © James Singleton. EUPL-1.2 (see the LICENSE file for the full license governing this code).
// © Martin Rowan. Updates for Owl Energy exported data.

using CsvHelper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace octoyosu
{
    public static class Program
    {
        // TODO: Determine if rates really include VAT, to ensure comparison is accurate.

        // Enter supplier rates.
        private const decimal currentSupplierUnitRate = 0.1146m;
        private const decimal currentSupplierStandingCharge = 0.19m;
        private const decimal ocotopusAgileStandingCharge = 0.21m;
        // Are input readings in UTC or localtime
        private const bool readingsTimeUtc = false;

        public static void Main(string[] args)
        {
            
            try
            {
                var sw = Stopwatch.StartNew();

                var readingsPath = "detailedReadings.csv";
                if (args.Length > 0)
                {
                    readingsPath = args[0];
                }

                var pricingPath =
                    Directory.GetFiles(".", "csv_agile_*.csv", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                if (args.Length > 1)
                {
                    pricingPath = args[1];
                }

                Console.WriteLine();
                Console.WriteLine("🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙 🐙");
                Console.WriteLine();

                var rates = new Dictionary<DateTime, decimal>();
                var usages = new List<Usage>();
                decimal lastKwh = 0;
                var lastTimeMinute = 99;
                var lastDay = 0;
                DateTime time;

                using (var reader = new StreamReader(readingsPath))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    csv.Read();
                    csv.ReadHeader();
                    var usage = new Usage
                    {
                        KWh = 0,
                        Time = DateTime.UnixEpoch,
                    };
                    while (csv.Read())
                    {
                        var kwh = csv.GetField<decimal>(3) /1000;  // Per Day Kwh counter
                        if (kwh <= 0) continue;


                        // TODO: Determine if time in Owl output file is UTC or Localtime
                        if (readingsTimeUtc)
                        {
                            time = DateTime.Parse(csv.GetField<string>(0)); // UTC
                        }
                        else
                        {
                            time = DateTime.Parse(csv.GetField<string>(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).ToUniversalTime(); // Local Time
                        }
                        time = time.AddSeconds(-time.Second);  // seconds, as used time is used as a key for finding matching rates

                        if ((time.Minute != 00 && time.Minute != 30) || (time.Minute == lastTimeMinute)) continue; // Only sample every 30 mins. Only use 1st value when multiple for a given minute.

                        if (time.Day != lastDay) lastKwh = 0;  // Reset the counter for a new day

                        usage = new Usage
                        {
                            KWh = kwh - lastKwh,
                            Time = time,
                        };
                        usages.Add(usage);
                        lastKwh = kwh; // Track last reading for kwh so that we can determine how much was used in each 30 min period.
                        lastDay = time.Day;

                        lastTimeMinute = time.Minute;
                    }
                }

                var min = usages.Min(u => u.Time);
                var max = usages.Max(u => u.Time);
                var totalDays = (decimal) (max - min).TotalDays;
                Console.WriteLine($"{min:ddd dd MMM yyyy} to {max:ddd dd MMM yyyy} ({totalDays:0} days)");

                var totalKwh = usages.Sum(u => u.KWh);
                PrintAverages(totalKwh, totalDays, "kWh");

                var currentSupplierTotalUnitCost = totalKwh * currentSupplierUnitRate;
                var currentSupplierTotalStandingCharge = totalDays * currentSupplierStandingCharge;
                var currentSupplierTotalCostGbpIncVat = currentSupplierTotalUnitCost + currentSupplierTotalStandingCharge;
                Console.WriteLine("Current Supplier:");
                PrintAverages(currentSupplierTotalCostGbpIncVat, totalDays, "GBP inc. VAT");

                Console.WriteLine("Loading agile pricing and calculating...");
                Console.WriteLine();

                using (var reader = new StreamReader(pricingPath))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    while (csv.Read())
                    {
                        time = DateTime.Parse(csv.GetField<string>(0)); // UTC

                        if (time >= min && time <= max)
                        {
                            rates.Add(time, csv.GetField<decimal>(4)); // inc. VAT
                        }
                    }
                }

                var octopusAgileTotalUnitCost = (
                    from usage in usages
                    let rate = rates.SingleOrDefault(r =>
                        r.Key == usage.Time)
                    select rate.Value * 0.01m * usage.KWh
                ).Sum();

                var octopusAgileTotalStandingCharge = totalDays * ocotopusAgileStandingCharge;
                var octopusAgileTotalCostIncVat = octopusAgileTotalUnitCost + octopusAgileTotalStandingCharge;
                Console.WriteLine("Agile:");
                PrintAverages(octopusAgileTotalCostIncVat, totalDays, "GBP inc. VAT");

                Console.WriteLine("Savings:");
                PrintAverages(currentSupplierTotalCostGbpIncVat - octopusAgileTotalCostIncVat, totalDays, "GBP inc. VAT");

                var ocotopusAgilePercentage = octopusAgileTotalCostIncVat / currentSupplierTotalCostGbpIncVat * 100;
                Console.WriteLine("Current Supplier 100%: 🐙🐙🐙🐙🐙🐙🐙🐙🐙🐙");
                Console.Write($"      Agile  {ocotopusAgilePercentage:0}%: ");
                for (var i = 10; i < ocotopusAgilePercentage; i += 10)
                {
                    Console.Write("🐙");
                }

                Console.WriteLine();
                Console.WriteLine();
                sw.Stop();

#if DEBUG
                Console.WriteLine($"Done in {sw.ElapsedMilliseconds:#,###}ms");
#endif
            }
            catch (FileNotFoundException fileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Could not find file: {fileNotFoundException.FileName}");
                Console.WriteLine("Usage: ./octoyosu [readingsFile.csv] [pricingFile.csv]");
            }
            catch (Exception exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"An unexpected error occurred: {exception}");
            }

            Console.ResetColor();
        }

        private static void PrintAverages(decimal total, decimal days, string unit)
        {
            var day = total / days;
            var year = day * 365; // ignore leap year
            var month = year / 12;
            Console.WriteLine($"Period total {total:0.00} {unit} (approx)");
            Console.WriteLine($"Daily average {day:0.00} {unit} (approx)");
            Console.WriteLine($"Yearly average {year:0} {unit} (approx)");
            Console.WriteLine($"Monthly average {month:0} {unit} (approx)");
            Console.WriteLine();
        }
    }
}
