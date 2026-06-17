using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HlaeObsTools.Services.Vmix;

public sealed class ReplayEventRegistry
{
    private readonly object _sync = new();
    private readonly List<ReplayEventRecord> _records = new();
    private long _nextId;

    public event EventHandler? Changed;

    public IReadOnlyList<ReplayEventRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records
                .OrderByDescending(r => r.CreatedUtc)
                .Select(r => r.Clone())
                .ToArray();
        }
    }

    public ReplayEventRecord Add(ReplayEventDraft draft)
    {
        ReplayEventRecord record;
        lock (_sync)
        {
            record = new ReplayEventRecord
            {
                LocalId = ++_nextId,
                CreatedUtc = DateTimeOffset.UtcNow,
                Source = draft.Source,
                Label = draft.Label,
                Channel = NormalizeChannel(draft.Channel),
                Camera = Math.Clamp(draft.Camera, 1, 8),
                Round = draft.Round,
                Status = string.IsNullOrWhiteSpace(draft.Status) ? "Created" : draft.Status,
                VmixEventId = draft.VmixEventId,
                KillCount = Math.Max(0, draft.KillCount)
            };
            _records.Add(record);
            TrimLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return record.Clone();
    }

    public void Update(long localId, Action<ReplayEventRecord> update)
    {
        lock (_sync)
        {
            var record = _records.FirstOrDefault(r => r.LocalId == localId);
            if (record == null)
                return;
            update(record);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _records.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimLocked()
    {
        const int maxRecords = 200;
        if (_records.Count <= maxRecords)
            return;

        _records.RemoveRange(0, _records.Count - maxRecords);
    }

    private static string NormalizeChannel(string? channel)
    {
        var value = string.IsNullOrWhiteSpace(channel) ? "A" : channel.Trim().ToUpperInvariant();
        return value is "A" or "B" or "AB" ? value : "A";
    }
}

public sealed class ReplayEventDraft
{
    public string Source { get; init; } = "Main";
    public string Label { get; init; } = string.Empty;
    public string Channel { get; init; } = "A";
    public int Camera { get; init; } = 1;
    public int Round { get; init; }
    public int KillCount { get; init; }
    public string? VmixEventId { get; init; }
    public string Status { get; init; } = "Created";
}

public sealed class ReplayEventRecord
{
    public long LocalId { get; set; }
    public string? VmixEventId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string Source { get; set; } = "Main";
    public string Label { get; set; } = string.Empty;
    public string Channel { get; set; } = "A";
    public int Camera { get; set; } = 1;
    public int Round { get; set; }
    public int KillCount { get; set; }
    public string Status { get; set; } = "Created";

    public ReplayEventRecord Clone()
    {
        return new ReplayEventRecord
        {
            LocalId = LocalId,
            VmixEventId = VmixEventId,
            CreatedUtc = CreatedUtc,
            Source = Source,
            Label = Label,
            Channel = Channel,
            Camera = Camera,
            Round = Round,
            KillCount = KillCount,
            Status = Status
        };
    }
}

public sealed class ReplayEventCollection : ObservableCollection<ReplayEventRecord>
{
}
