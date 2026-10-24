namespace BluetoothBatteryTray;
internal static class BatteryInsights
{
    public static object Calculate(IReadOnlyList<BatterySample> source, int days)
    {
        var data=source.Where(x=>x.Time >= DateTime.Now.AddDays(-days)).OrderBy(x=>x.Time).ToArray();
        if (data.Length==0) return new {samples=0, latest=(int?)null, drainPerHour=(double?)null, remainingHours=(double?)null, updated=(DateTime?)null};
        var latest=data[^1];
        // Fit only the current discharging run. Charging and flat intervals
        // cannot provide a meaningful remaining-time estimate.
        var run=new List<BatterySample>{latest};
        for(int i=data.Length-2;i>=0;i--)
        {
            if (data[i].Level < run[^1].Level || (run[^1].Time-data[i].Time).TotalHours > 12) break;
            run.Add(data[i]);
        }
        run.Reverse();
        double hours=(run[^1].Time-run[0].Time).TotalHours;
        double drop=run[0].Level-run[^1].Level;
        double? rate = hours>=0.5 && drop>=3 ? Math.Round(drop/hours,2) : null;
        return new {samples=data.Length,latest=(int?)latest.Level,drainPerHour=rate,
            remainingHours=rate.HasValue ? (double?)Math.Round(latest.Level/rate.Value,1):null,updated=(DateTime?)latest.Time};
    }
}
