using System.Text;

namespace CompanionOS.Core.Diagnostics;

/// <summary>
/// Appends daily JSON-lines diagnostics and keeps the active file within a known bound.
/// The previous segment is retained beside it so a long session cannot fill the user's SSD.
/// </summary>
public sealed class RollingJsonLinesWriter
{
    private const int RetainedDays = 7;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maximumBytes;
    private string _dayStamp = string.Empty;
    private string _activePath = string.Empty;

    public RollingJsonLinesWriter(string directory, string baseName, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (maximumBytes < 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "A log segment must allow at least 128 bytes.");
        }

        _directory = Path.GetFullPath(directory);
        _baseName = baseName;
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_directory);
    }

    public string ActivePath
    {
        get { lock (_gate) { return _activePath; } }
    }

    public string Append(DateTimeOffset timestamp, string jsonLine)
    {
        ArgumentNullException.ThrowIfNull(jsonLine);
        string line = jsonLine.TrimEnd('\r', '\n');

        lock (_gate)
        {
            string stamp = timestamp.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            if (!stamp.Equals(_dayStamp, StringComparison.Ordinal))
            {
                _dayStamp = stamp;
                _activePath = Path.Combine(_directory, $"{_baseName}-{stamp}.jsonl");
                RemoveExpiredFiles(timestamp.Date.AddDays(-(RetainedDays - 1)));
            }

            long lineBytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (lineBytes > _maximumBytes)
            {
                throw new InvalidDataException("A single diagnostic event exceeds the configured log segment size.");
            }

            if (File.Exists(_activePath))
            {
                long currentBytes = new FileInfo(_activePath).Length;
                if (currentBytes > 0 && currentBytes + lineBytes > _maximumBytes)
                {
                    string previousPath = Path.Combine(_directory, $"{_baseName}-{stamp}.previous.jsonl");
                    if (currentBytes <= _maximumBytes)
                    {
                        File.Move(_activePath, previousPath, overwrite: true);
                    }
                    else
                    {
                        // Discard an oversized legacy/corrupt segment so the on-disk cap is real.
                        File.Delete(_activePath);
                        if (File.Exists(previousPath)) File.Delete(previousPath);
                    }
                }
            }

            using FileStream stream = new(_activePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(line);
            return _activePath;
        }
    }

    private void RemoveExpiredFiles(DateTime oldestRetainedDay)
    {
        foreach (string path in Directory.EnumerateFiles(_directory, $"{_baseName}-*.jsonl"))
        {
            string name = Path.GetFileName(path);
            string prefix = _baseName + "-";
            if (name.Length < prefix.Length + 8 || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !DateOnly.TryParseExact(name.AsSpan(prefix.Length, 8), "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateOnly day))
            {
                continue;
            }

            if (day.ToDateTime(TimeOnly.MinValue) < oldestRetainedDay)
            {
                File.Delete(path);
            }
        }
    }
}
