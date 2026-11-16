using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Abstractions;

namespace Braid.HostPrivacyProbe;

/// <summary>Actual framework observers; raw operands are inspected synchronously and never retained/exported.</summary>
internal sealed class HostPrivacyObservation : EventListener, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly object sync = new();
    private readonly string marker;
    private readonly List<IDisposable> subscriptions = [];
    private readonly List<DiagnosticListener> incoming = [];
    private readonly ActivityListener activities;
    private readonly IDisposable all;
    private bool initialized;
    private int observations;
    internal bool CountIncoming = true;
    internal int IncomingGraphs, IncomingActivities, OutgoingGraphs, OutgoingActivities, IdentityEvents, SafeHttpStops, SafeServerStops, LateAttempts;
    internal bool IncomingMarker, ReferenceContentGraph, IdentityEventMarker, UnsafeEventMarker, Failed;
    internal HostIdentityMarkerLogger? IdentityLogger;
    internal HttpRequestMessage? ExpectedReferenceRequest;
    internal TaskCompletionSource<IHost> Built { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal HostPrivacyObservation(string marker)
    {
        this.marker = marker;
        activities = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = ObserveActivity
        };
        ActivitySource.AddActivityListener(activities);
        all = DiagnosticListener.AllListeners.Subscribe(this);
        initialized = true;
        foreach (var source in EventSource.GetSources()) Enable(source);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name is "Microsoft.Extensions.Hosting" or "Microsoft.AspNetCore" or "HttpHandlerDiagnosticListener")
        {
            lock (sync)
            {
                if (listener.Name == "Microsoft.AspNetCore") incoming.Add(listener);
                subscriptions.Add(listener.Subscribe(this, _ => true));
            }
        }
    }

    internal void LateSubscribe()
    {
        lock (sync)
            foreach (var listener in incoming)
            { LateAttempts++; subscriptions.Add(listener.Subscribe(this, _ => true)); }
    }

    public void OnNext(KeyValuePair<string, object?> item)
    {
        try
        {
            if (item.Key == "HostBuilt" && item.Value is IHost host) { Built.TrySetResult(host); return; }
            if (Interlocked.Increment(ref observations) > 10_000) { Failed = true; return; }
            object? payload = item.Value;
            HttpContext? context = payload as HttpContext ?? Property(payload, "HttpContext") as HttpContext ?? Property(payload, "httpContext") as HttpContext;
            if (CountIncoming && context is not null)
            {
                Interlocked.Increment(ref IncomingGraphs);
                if ((context.Request.QueryString.Value?.Contains(marker, StringComparison.Ordinal) ?? false)
                    || context.Request.Headers.Cookie.Any(value => value?.Contains(marker, StringComparison.Ordinal) == true)) IncomingMarker = true;
            }
            HttpRequestMessage? request = payload as HttpRequestMessage ?? Property(payload, "Request") as HttpRequestMessage;
            request ??= (Property(payload, "Response") as HttpResponseMessage)?.RequestMessage;
            if (request is not null && OwnedProvider(request.RequestUri))
            {
                Interlocked.Increment(ref OutgoingGraphs);
                if (ReferenceEquals(request, ExpectedReferenceRequest) && ReferenceEquals(request.Content, ExpectedReferenceRequest.Content)
                    && request.Content is not null) ReferenceContentGraph = true;
            }
        }
        catch (Exception) { Failed = true; }
    }

    private static object? Property(object? value, string name) => value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);

    private static bool OwnedProvider(Uri? uri) => uri is not null && uri.Scheme == "https" && uri.Host == "login.microsoftonline.com"
        && !uri.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal);

    private void ObserveActivity(Activity activity)
    {
        try
        {
            if (CountIncoming && activity.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn") Interlocked.Increment(ref IncomingActivities);
            if (activity.Source.Name != "System.Net.Http") return;
            string? address = activity.GetTagItem("url.full") as string ?? activity.GetTagItem("http.url") as string;
            if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) && OwnedProvider(uri))
                Interlocked.Increment(ref OutgoingActivities);
        }
        catch (Exception) { Failed = true; }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (initialized) Enable(eventSource);
    }

    private void Enable(EventSource source)
    {
        if (source.Name is "Microsoft.AspNetCore.Hosting" or "Microsoft-AspNetCore-Server-Kestrel" or "System.Net.Http" or "Microsoft.IdentityModel.EventSource")
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs item)
    {
        try
        {
            // Approved export projection: count fixed known scalar/empty events, never copy arbitrary payloads.
            if (item.EventSource.Name == "System.Net.Http" && item.EventId == 2 && item.Payload?.Count == 1 && item.Payload[0] is int)
                Interlocked.Increment(ref SafeHttpStops);
            if (item.EventSource.Name == "Microsoft.AspNetCore.Hosting" && item.EventId == 4 && item.Payload?.Count == 0)
                Interlocked.Increment(ref SafeServerStops);
            if (item.Payload is null) return;
            if (item.EventSource.Name == "Microsoft.IdentityModel.EventSource") Interlocked.Increment(ref IdentityEvents);
            bool contains = item.Payload.OfType<string>().Any(value => value.Contains(marker, StringComparison.Ordinal));
            if (contains && item.EventSource.Name == "Microsoft.IdentityModel.EventSource") IdentityEventMarker = true;
            if (contains) UnsafeEventMarker = true; // Boolean evidence only; raw EventSource strings are NOT approved exports.
        }
        catch (Exception) { Failed = true; }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { Failed = true; }
    public override void Dispose()
    {
        all.Dispose(); activities.Dispose();
        lock (sync) foreach (var subscription in subscriptions) subscription.Dispose();
        base.Dispose();
    }
}

internal sealed class HostIdentityMarkerLogger(string marker) : IIdentityLogger
{
    internal bool MarkerSeen;
    public bool IsEnabled(EventLogLevel level) => true;
    public void Log(LogEntry entry)
    {
        if (entry.Message?.Contains(marker, StringComparison.Ordinal) == true) MarkerSeen = true;
    }
}

internal sealed class HostMarkerWriter(TextWriter actual, string marker) : TextWriter
{
    private readonly object sync = new();
    private string tail = "";
    private readonly StringBuilder captured = new();
    private int count;
    internal bool MarkerSeen, Overflow;
    public override Encoding Encoding => actual.Encoding;
    public override void Write(char value) => Write(value.ToString());
    public override void Write(string? value)
    {
        if (value is null) return;
        lock (sync)
        {
            count += value.Length; if (count > 32_768) Overflow = true;
            if (!Overflow) captured.Append(value);
            string window = tail + value;
            if (window.Contains(marker, StringComparison.Ordinal)) MarkerSeen = true;
            tail = window.Length > marker.Length ? window[^marker.Length..] : window;
            actual.Write(value);
        }
    }
    internal void CheckPrivate(IEnumerable<string> operands)
    {
        lock (sync)
        {
            string text = captured.ToString();
            if (operands.Where(value => value.Length != 0).Any(value => text.Contains(value, StringComparison.Ordinal))) MarkerSeen = true;
        }
    }
    public override void WriteLine(string? value) { Write(value); Write(NewLine); }
    protected override void Dispose(bool disposing) { if (disposing) actual.Flush(); base.Dispose(disposing); }
}
