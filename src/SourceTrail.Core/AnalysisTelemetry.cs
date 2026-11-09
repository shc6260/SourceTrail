using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SourceTrail.Core;

/// <summary>Records stage costs without source paths, contents or connection information.</summary>
public static class AnalysisTelemetry
{
    private const string MeterName = "SourceTrail.Analysis";
    private static readonly Meter Meter = new(MeterName, "1.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("sourcetrail.stage.duration", "ms");
    private static readonly Counter<long> Files = Meter.CreateCounter<long>("sourcetrail.input.files");
    private static readonly Counter<long> Bytes = Meter.CreateCounter<long>("sourcetrail.input.bytes", "By");
    private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("sourcetrail.cache.hits");

    public static IDisposable Measure(string stage) => new Measurement(stage);
    public static void InputRead(long files, long bytes)
    {
        Files.Add(files);
        Bytes.Add(bytes);
    }
    public static void CacheHit(string cache) => CacheHits.Add(1, new KeyValuePair<string, object?>("cache", cache));

    public static IDisposable Listen(Action<string> log)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            // Cache hits remain available to exporters without logging once per graph node.
            if (instrument.Meter.Name == MeterName && instrument.Name != "sourcetrail.cache.hits")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? stage = null;
            foreach (var tag in tags) if (tag.Key == "stage") stage = tag.Value as string;
            log($"Analysis stage {stage}: {value:F3} ms.");
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            log($"Analysis counter {instrument.Name}: {value}."));
        listener.Start();
        return listener;
    }

    private sealed class Measurement(string stage) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose() => Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", stage));
    }
}
