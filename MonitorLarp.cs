using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace MonitorLarp
{
    internal enum ActivityKind
    {
        Read,
        Write
    }

    internal sealed class RecentSample
    {
        public DateTime Utc;
        public long Bytes;
        public ActivityKind Kind;
    }

    internal static class HistoryLogger
    {
        private static readonly object _fileGate = new object();
        public static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MonitorLarp_history.log");

        public static void Append(IEnumerable<string> lines)
        {
            try
            {
                lock (_fileGate)
                {
                    if (!File.Exists(LogPath))
                    {
                        File.WriteAllText(LogPath, "Time\tProcess\tPID\tWritten (bytes)\tRead (bytes)\tWrite Ops\tRead Ops\tPath\r\n");
                    }
                    File.AppendAllLines(LogPath, lines);
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class ActivityRecord
    {
        public readonly string Path;
        public readonly int ProcessId;
        public readonly string ProcessName;
        public long ReadBytes;
        public long WriteBytes;
        public int ReadOperations;
        public int WriteOperations;
        public DateTime LastReadUtc;
        public DateTime LastWriteUtc;
        public DateTime LastActivityUtc;
        public long? CachedSize;
        public DateTime LastSizeCheckUtc;
        public readonly Queue<RecentSample> Recent = new Queue<RecentSample>();

        public long? GetOrUpdateSize(DateTime nowUtc)
        {
            if ((nowUtc - LastSizeCheckUtc).TotalSeconds < 30 && LastSizeCheckUtc != DateTime.MinValue)
                return CachedSize;

            LastSizeCheckUtc = nowUtc;
            CachedSize = EtwFileMonitor.GetFileSize(Path);
            return CachedSize;
        }

        public ActivityRecord(string path, int processId, string processName)
        {
            Path = path;
            ProcessId = processId;
            ProcessName = string.IsNullOrWhiteSpace(processName) ? "PID " + processId : processName;
        }

        public void Add(ActivityKind kind, long bytes, DateTime utc)
        {
            if (bytes <= 0)
                return;

            if (kind == ActivityKind.Write)
            {
                WriteBytes += bytes;
                WriteOperations++;
                LastWriteUtc = utc;
            }
            else
            {
                ReadBytes += bytes;
                ReadOperations++;
                LastReadUtc = utc;
            }

            LastActivityUtc = utc;
            Recent.Enqueue(new RecentSample { Kind = kind, Bytes = bytes, Utc = utc });
            TrimRecent(utc);
        }

        public void TrimRecent(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc.AddSeconds(-5);
            while (Recent.Count > 0 && Recent.Peek().Utc < cutoff)
                Recent.Dequeue();
        }

        public long RecentBytes(ActivityKind kind, DateTime nowUtc)
        {
            TrimRecent(nowUtc);
            long total = 0;
            foreach (RecentSample sample in Recent)
            {
                if (sample.Kind == kind)
                    total += sample.Bytes;
            }

            return total;
        }
    }

    internal sealed class ActivityRow
    {
        public string Path { get; set; }
        public string Process { get; set; }
        public long? CurrentSize { get; set; }
        public long ReadBytes { get; set; }
        public long WriteBytes { get; set; }
        public int ReadOperations { get; set; }
        public int WriteOperations { get; set; }
        public long ReadRate { get; set; }
        public long WriteRate { get; set; }
        public DateTime? LastRead { get; set; }
        public DateTime? LastWrite { get; set; }
        public bool ActiveRead { get; set; }
        public bool ActiveWrite { get; set; }
    }

    internal sealed class MonitorStatistics
    {
        public int Records;
        public long ReadBytes;
        public long WriteBytes;
        public long ReadOperations;
        public long WriteOperations;
        public DateTime? LastEvent;
    }

    internal enum FilterMode
    {
        AnyActivity,
        CurrentSize,
        WrittenBytes,
        ReadBytes,
        CurrentSizeAndWritten,
        CurrentSizeAndRead
    }

    internal enum GroupMode
    {
        Folder,
        SeparateFiles,
        FolderAndProcess
    }

    internal sealed class FilterSettings
    {
        public bool ShowWrites;
        public bool ShowReads;
        public bool OnlyActive;
        public int ActiveSeconds;
        public long ThresholdBytes;
        public FilterMode Mode;
        public GroupMode GroupMode;
        public string[] Roots;
    }

    internal sealed class EtwFileMonitor : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, ActivityRecord> _records =
            new Dictionary<string, ActivityRecord>(StringComparer.OrdinalIgnoreCase);
        private Thread _worker;
        private System.Threading.Timer _cleanupTimer;
        private TraceEventSession _session;
        private ETWTraceEventSource _source;
        private volatile bool _stopping;
        private volatile bool _captureWrites = true;
        private volatile bool _captureReads;
        private string[] _roots = new string[0];

        public event Action<string> Error;
        public event Action<bool> RunningChanged;

        public bool IsRunning
        {
            get { return _worker != null && _worker.IsAlive; }
        }

        public void SetCapture(bool writes, bool reads)
        {
            _captureWrites = writes;
            _captureReads = reads;
        }

        public void SetRoots(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _roots = new string[0];
                return;
            }

            _roots = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeRoot)
                .Where(s => s.Length > 0)
                .ToArray();
        }

        public void Start()
        {
            if (IsRunning)
                return;

            _stopping = false;
            _cleanupTimer = new System.Threading.Timer(CleanupCallback, null, 30000, 30000);
            _worker = new Thread(RunTrace)
            {
                IsBackground = true,
                Name = "MonitorLarp ETW"
            };
            _worker.Start();
        }

        public void Stop()
        {
            _stopping = true;
            try
            {
                if (_cleanupTimer != null)
                {
                    _cleanupTimer.Dispose();
                    _cleanupTimer = null;
                }
            }
            catch
            {
            }

            try
            {
                if (_source != null)
                    _source.StopProcessing();
            }
            catch
            {
            }

            try
            {
                if (_session != null)
                    _session.Dispose();
            }
            catch
            {
            }

            Thread worker = _worker;
            if (worker != null && worker != Thread.CurrentThread)
                worker.Join(2000);
        }

        public void Clear()
        {
            List<ActivityRecord> toEvict;
            lock (_gate)
            {
                toEvict = _records.Values.ToList();
                _records.Clear();
            }
            WriteRecordsToHistory(toEvict);
        }

        private void CleanupCallback(object state)
        {
            try
            {
                EvictOldRecords();
            }
            catch
            {
            }
        }

        private void EvictOldRecords()
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-15);
            List<ActivityRecord> evicted = new List<ActivityRecord>();

            lock (_gate)
            {
                List<string> keysToRemove = new List<string>();
                foreach (KeyValuePair<string, ActivityRecord> pair in _records)
                {
                    if (pair.Value.LastActivityUtc < cutoff)
                    {
                        keysToRemove.Add(pair.Key);
                        evicted.Add(pair.Value);
                    }
                }

                foreach (string key in keysToRemove)
                {
                    _records.Remove(key);
                }

                if (_records.Count > 30000)
                {
                    var oldest = _records
                        .OrderBy(p => p.Value.LastActivityUtc)
                        .Take(_records.Count - 25000)
                        .ToList();

                    foreach (var pair in oldest)
                    {
                        _records.Remove(pair.Key);
                        evicted.Add(pair.Value);
                    }
                }
            }

            WriteRecordsToHistory(evicted);
        }

        private static void WriteRecordsToHistory(List<ActivityRecord> records)
        {
            if (records == null || records.Count == 0)
                return;

            List<string> lines = new List<string>(records.Count);
            foreach (ActivityRecord rec in records)
            {
                DateTime last = rec.LastWriteUtc > rec.LastReadUtc ? rec.LastWriteUtc : rec.LastReadUtc;
                if (last == DateTime.MinValue)
                    last = rec.LastActivityUtc;
                string timeStr = (last == DateTime.MinValue ? DateTime.Now : last.ToLocalTime()).ToString("yyyy-MM-dd HH:mm:ss");
                lines.Add(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}",
                    timeStr,
                    rec.ProcessName,
                    rec.ProcessId,
                    rec.WriteBytes,
                    rec.ReadBytes,
                    rec.WriteOperations,
                    rec.ReadOperations,
                    rec.Path));
            }

            HistoryLogger.Append(lines);
        }

        public List<ActivityRow> GetRows(FilterSettings settings)
        {
            DateTime now = DateTime.UtcNow;
            List<ActivityRecord> source;
            lock (_gate)
            {
                source = _records.Values.ToList();
            }

            bool needsSizeForFilter = settings.Mode == FilterMode.CurrentSize ||
                                      settings.Mode == FilterMode.CurrentSizeAndWritten ||
                                      settings.Mode == FilterMode.CurrentSizeAndRead;

            List<ActivityRow> rawRows = new List<ActivityRow>(source.Count);
            foreach (ActivityRecord record in source)
            {
                if (!IsUnderRoots(record.Path, settings.Roots))
                    continue;

                ActivityRow row;
                lock (record)
                {
                    record.TrimRecent(now);
                    row = new ActivityRow
                    {
                        Path = record.Path,
                        Process = record.ProcessName + " (" + record.ProcessId + ")",
                        ReadBytes = record.ReadBytes,
                        WriteBytes = record.WriteBytes,
                        ReadOperations = record.ReadOperations,
                        WriteOperations = record.WriteOperations,
                        ReadRate = record.RecentBytes(ActivityKind.Read, now) / 5,
                        WriteRate = record.RecentBytes(ActivityKind.Write, now) / 5,
                        LastRead = record.LastReadUtc == DateTime.MinValue ? (DateTime?)null : record.LastReadUtc.ToLocalTime(),
                        LastWrite = record.LastWriteUtc == DateTime.MinValue ? (DateTime?)null : record.LastWriteUtc.ToLocalTime(),
                        ActiveRead = settings.ShowReads && record.LastReadUtc >= now.AddSeconds(-settings.ActiveSeconds),
                        ActiveWrite = settings.ShowWrites && record.LastWriteUtc >= now.AddSeconds(-settings.ActiveSeconds),
                        CurrentSize = needsSizeForFilter ? record.GetOrUpdateSize(now) : record.CachedSize
                    };
                }

                rawRows.Add(row);
            }

            List<ActivityRow> rows = settings.GroupMode == GroupMode.SeparateFiles
                ? rawRows
                : GroupRows(rawRows, settings.GroupMode);

            rows = rows.Where(row =>
            {
                bool hasSelectedActivity =
                    (settings.ShowWrites && row.WriteBytes > 0) ||
                    (settings.ShowReads && row.ReadBytes > 0);
                if (!hasSelectedActivity)
                    return false;

                if (settings.OnlyActive && !row.ActiveWrite && !row.ActiveRead)
                    return false;

                return PassesThreshold(row, settings);
            }).ToList();

            List<ActivityRow> result = rows
                .OrderByDescending(r => r.WriteBytes)
                .ThenByDescending(r => r.ReadBytes)
                .Take(500)
                .ToList();

            if (!needsSizeForFilter)
            {
                foreach (ActivityRow r in result)
                {
                    if (!r.CurrentSize.HasValue && !r.Path.StartsWith("[FOLDER TOTAL:", StringComparison.Ordinal))
                    {
                        r.CurrentSize = GetFileSize(r.Path);
                    }
                }
            }

            return result;
        }

        private static List<ActivityRow> GroupRows(List<ActivityRow> rows, GroupMode mode)
        {
            IEnumerable<IGrouping<string, ActivityRow>> groups = rows.GroupBy(
                row => mode == GroupMode.FolderAndProcess
                    ? GetFolderPath(row.Path) + "\0" + row.Process
                    : GetFolderPath(row.Path),
                StringComparer.OrdinalIgnoreCase);

            List<ActivityRow> result = new List<ActivityRow>();
            foreach (IGrouping<string, ActivityRow> group in groups)
            {
                List<ActivityRow> items = group.ToList();
                string folder = GetFolderPath(items[0].Path);
                string process = items.Select(item => item.Process).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    ? items[0].Process
                    : "Multiple processes (" + items.Select(item => item.Process).Distinct(StringComparer.OrdinalIgnoreCase).Count() + ")";

                result.Add(new ActivityRow
                {
                    Path = "[FOLDER TOTAL: " + items.Count + " files] " + folder,
                    Process = process,
                    CurrentSize = SumCurrentSize(items),
                    ReadBytes = Sum(items.Select(item => item.ReadBytes)),
                    WriteBytes = Sum(items.Select(item => item.WriteBytes)),
                    ReadOperations = (int)Math.Min(int.MaxValue, Sum(items.Select(item => (long)item.ReadOperations))),
                    WriteOperations = (int)Math.Min(int.MaxValue, Sum(items.Select(item => (long)item.WriteOperations))),
                    ReadRate = Sum(items.Select(item => item.ReadRate)),
                    WriteRate = Sum(items.Select(item => item.WriteRate)),
                    LastRead = items.Where(item => item.LastRead.HasValue).Select(item => item.LastRead).OrderByDescending(value => value).FirstOrDefault(),
                    LastWrite = items.Where(item => item.LastWrite.HasValue).Select(item => item.LastWrite).OrderByDescending(value => value).FirstOrDefault(),
                    ActiveRead = items.Any(item => item.ActiveRead),
                    ActiveWrite = items.Any(item => item.ActiveWrite)
                });
            }

            return result;
        }

        private static long Sum(IEnumerable<long> values)
        {
            long total = 0;
            foreach (long value in values)
            {
                if (value > 0 && total > long.MaxValue - value)
                    return long.MaxValue;
                total += value;
            }

            return total;
        }

        private static long? SumCurrentSize(IEnumerable<ActivityRow> rows)
        {
            bool found = false;
            long total = 0;
            foreach (ActivityRow row in rows)
            {
                if (!row.CurrentSize.HasValue)
                    continue;
                found = true;
                long value = row.CurrentSize.Value;
                if (value > 0 && total > long.MaxValue - value)
                    return long.MaxValue;
                total += value;
            }

            return found ? (long?)total : null;
        }

        private static string GetFolderPath(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(folder))
                    return folder.EndsWith("\\", StringComparison.Ordinal) ? folder : folder + "\\";
            }
            catch
            {
            }

            int separator = (path ?? string.Empty).LastIndexOf('\\');
            return separator >= 0 ? path.Substring(0, separator + 1) : path;
        }

        public MonitorStatistics GetStatistics()
        {
            MonitorStatistics statistics = new MonitorStatistics();
            lock (_gate)
            {
                statistics.Records = _records.Count;
                foreach (ActivityRecord record in _records.Values)
                {
                    lock (record)
                    {
                        statistics.ReadBytes += record.ReadBytes;
                        statistics.WriteBytes += record.WriteBytes;
                        statistics.ReadOperations += record.ReadOperations;
                        statistics.WriteOperations += record.WriteOperations;
                        DateTime last = record.LastReadUtc > record.LastWriteUtc
                            ? record.LastReadUtc
                            : record.LastWriteUtc;
                        if (last != DateTime.MinValue &&
                            (!statistics.LastEvent.HasValue || last > statistics.LastEvent.Value.ToUniversalTime()))
                            statistics.LastEvent = last.ToLocalTime();
                    }
                }
            }

            return statistics;
        }

        private bool PassesThreshold(ActivityRow row, FilterSettings settings)
        {
            long size = row.CurrentSize.GetValueOrDefault();
            switch (settings.Mode)
            {
                case FilterMode.AnyActivity:
                    return true;
                case FilterMode.WrittenBytes:
                    return settings.ShowWrites && row.WriteBytes >= settings.ThresholdBytes;
                case FilterMode.ReadBytes:
                    return settings.ShowReads && row.ReadBytes >= settings.ThresholdBytes;
                case FilterMode.CurrentSizeAndWritten:
                    return row.CurrentSize.HasValue && size >= settings.ThresholdBytes &&
                           settings.ShowWrites && row.WriteBytes >= settings.ThresholdBytes;
                case FilterMode.CurrentSizeAndRead:
                    return row.CurrentSize.HasValue && size >= settings.ThresholdBytes &&
                           settings.ShowReads && row.ReadBytes >= settings.ThresholdBytes;
                default:
                    return row.CurrentSize.HasValue && size >= settings.ThresholdBytes;
            }
        }

        private void RunTrace()
        {
            string sessionName = "MonitorLarp_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N");
            try
            {
                using (TraceEventSession session = new TraceEventSession(sessionName))
                {
                    _session = session;
                    session.StopOnDispose = true;
                    session.EnableKernelProvider(
                        KernelTraceEventParser.Keywords.FileIOInit |
                        KernelTraceEventParser.Keywords.FileIO |
                        KernelTraceEventParser.Keywords.DiskFileIO |
                        KernelTraceEventParser.Keywords.Process);

                    _source = session.Source;
                    _source.Kernel.FileIOWrite += data => Consume(data, ActivityKind.Write);
                    _source.Kernel.FileIORead += data => Consume(data, ActivityKind.Read);
                    RunningChanged?.Invoke(true);
                    _source.Process();
                }
            }
            catch (Exception ex)
            {
                if (!_stopping)
                    Error?.Invoke(ex.Message);
            }
            finally
            {
                _source = null;
                _session = null;
                RunningChanged?.Invoke(false);
            }
        }

        private void Consume(FileIOReadWriteTraceData data, ActivityKind kind)
        {
            if (kind == ActivityKind.Write && !_captureWrites)
                return;
            if (kind == ActivityKind.Read && !_captureReads)
                return;
            if (data.IoSize <= 0 || string.IsNullOrWhiteSpace(data.FileName))
                return;

            string path = data.FileName.Trim();
            if (path.EndsWith("\\", StringComparison.Ordinal))
                return;

            if (path.IndexOf("$", StringComparison.Ordinal) >= 0 ||
                path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) ||
                path.IndexOf("pagefile.sys", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("swapfile.sys", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            if (!IsUnderRoots(path, _roots))
                return;
            if (LooksLikeDirectory(path))
                return;

            int pid = data.ProcessID;
            string processName = ResolveProcessName(pid, data.ProcessName);
            string key = pid + "\0" + path;
            DateTime now = DateTime.UtcNow;

            lock (_gate)
            {
                ActivityRecord record;
                if (!_records.TryGetValue(key, out record))
                {
                    record = new ActivityRecord(path, pid, processName);
                    _records.Add(key, record);
                }

                lock (record)
                {
                    record.Add(kind, data.IoSize, now);
                }
            }
        }

        private static bool LooksLikeDirectory(string path)
        {
            if (path.EndsWith("\\", StringComparison.Ordinal))
                return true;

            try
            {
                if (Path.HasExtension(path))
                    return false;
                return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveProcessName(int processId, string eventProcessName)
        {
            if (!string.IsNullOrWhiteSpace(eventProcessName))
                return eventProcessName;

            try
            {
                return Process.GetProcessById(processId).ProcessName;
            }
            catch
            {
                return "PID " + processId;
            }
        }

        internal static long? GetFileSize(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                return fi.Exists ? (long?)fi.Length : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUnderRoots(string path, string[] roots)
        {
            if (roots == null || roots.Length == 0)
                return true;

            foreach (string root in roots)
            {
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string NormalizeRoot(string value)
        {
            string root = (value ?? string.Empty).Trim();
            if (root.Length == 2 && root[1] == ':')
                root += "\\";
            if (root.Length == 0)
                return string.Empty;
            if (!root.EndsWith("\\", StringComparison.Ordinal))
                root += "\\";
            return root;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly EtwFileMonitor _monitor = new EtwFileMonitor();
        private readonly TextBox _rootsText = new TextBox();
        private readonly Button _startButton = new Button();
        private readonly Button _stopButton = new Button();
        private readonly Button _clearButton = new Button();
        private readonly Button _testButton = new Button();
        private readonly Button _historyButton = new Button();
        private readonly CheckBox _writesCheck = new CheckBox();
        private readonly CheckBox _readsCheck = new CheckBox();
        private readonly CheckBox _bytesCheck = new CheckBox();
        private readonly CheckBox _activeCheck = new CheckBox();
        private readonly NumericUpDown _activeSeconds = new NumericUpDown();
        private readonly NumericUpDown _thresholdValue = new NumericUpDown();
        private readonly ComboBox _thresholdUnit = new ComboBox();
        private readonly ComboBox _modeCombo = new ComboBox();
        private readonly ComboBox _groupCombo = new ComboBox();
        private readonly Label _status = new Label();
        private readonly DataGridView _grid = new DataGridView();
        private readonly System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer();
        private volatile bool _isRefreshing;
        private volatile bool _refreshPending;
        private string _testFilePath;

        public MainForm()
        {
            Text = "MonitorLarp - File Read and Write Activity Monitor";
            Width = 1500;
            Height = 800;
            MinimumSize = new Size(1000, 600);
            StartPosition = FormStartPosition.CenterScreen;

            BuildControls();
            WireEvents();
            ApplyCaptureSettings();
            UpdateButtons(false);
        }

        private void BuildControls()
        {
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            FlowLayoutPanel top = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                Padding = new Padding(0, 0, 0, 4)
            };
            layout.Controls.Add(top, 0, 0);

            top.Controls.Add(new Label { Text = "Paths (empty = all drives):", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            _rootsText.Width = 260;
            _rootsText.Text = string.Empty;
            top.Controls.Add(_rootsText);

            _startButton.Text = "Start";
            _startButton.AutoSize = true;
            top.Controls.Add(_startButton);
            _stopButton.Text = "Stop";
            _stopButton.AutoSize = true;
            top.Controls.Add(_stopButton);
            _clearButton.Text = "Clear";
            _clearButton.AutoSize = true;
            top.Controls.Add(_clearButton);
            _testButton.Text = "Self-Test";
            _testButton.AutoSize = true;
            top.Controls.Add(_testButton);
            _historyButton.Text = "History Log";
            _historyButton.AutoSize = true;
            top.Controls.Add(_historyButton);

            FlowLayoutPanel filters = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                Padding = new Padding(0, 0, 0, 7)
            };
            layout.Controls.Add(filters, 0, 1);

            _writesCheck.Text = "Show Writes";
            _writesCheck.AutoSize = true;
            _writesCheck.Checked = true;
            filters.Controls.Add(_writesCheck);

            _readsCheck.Text = "Show Reads";
            _readsCheck.AutoSize = true;
            _readsCheck.Checked = false;
            filters.Controls.Add(_readsCheck);

            _bytesCheck.Text = "Show Bytes";
            _bytesCheck.AutoSize = true;
            _bytesCheck.Checked = true;
            filters.Controls.Add(_bytesCheck);

            _activeCheck.Text = "Active only (hide idle)";
            _activeCheck.AutoSize = true;
            _activeCheck.Checked = false;
            filters.Controls.Add(_activeCheck);
            filters.Controls.Add(new Label { Text = "last", AutoSize = true, Padding = new Padding(4, 7, 0, 0) });

            _activeSeconds.Minimum = 1;
            _activeSeconds.Maximum = 60;
            _activeSeconds.Value = 5;
            _activeSeconds.Width = 55;
            filters.Controls.Add(_activeSeconds);
            filters.Controls.Add(new Label { Text = "sec | filter:", AutoSize = true, Padding = new Padding(4, 7, 0, 0) });

            _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _modeCombo.Width = 245;
            _modeCombo.Items.Add("All files (no threshold)");
            _modeCombo.Items.Add("Current file size");
            _modeCombo.Items.Add("Written during session");
            _modeCombo.Items.Add("Read during session");
            _modeCombo.Items.Add("Size AND written during session");
            _modeCombo.Items.Add("Size AND read during session");
            _modeCombo.SelectedIndex = 0;
            filters.Controls.Add(_modeCombo);

            filters.Controls.Add(new Label { Text = "group by:", AutoSize = true, Padding = new Padding(4, 7, 0, 0) });
            _groupCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _groupCombo.Width = 245;
            _groupCombo.Items.Add("Folder total");
            _groupCombo.Items.Add("Separate files");
            _groupCombo.Items.Add("Folder and process");
            _groupCombo.SelectedIndex = 0;
            filters.Controls.Add(_groupCombo);

            filters.Controls.Add(new Label { Text = "min:", AutoSize = true, Padding = new Padding(4, 7, 0, 0) });
            _thresholdValue.Minimum = 0;
            _thresholdValue.Maximum = 1000000000;
            _thresholdValue.Value = 500;
            _thresholdValue.Width = 85;
            filters.Controls.Add(_thresholdValue);

            _thresholdUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            _thresholdUnit.Width = 70;
            _thresholdUnit.Items.Add("MB");
            _thresholdUnit.Items.Add("GB");
            _thresholdUnit.SelectedIndex = 0;
            filters.Controls.Add(_thresholdUnit);

            _status.AutoSize = true;
            _status.ForeColor = Color.DimGray;
            _status.Padding = new Padding(10, 7, 0, 0);
            filters.Controls.Add(_status);

            ConfigureGrid();
            layout.Controls.Add(_grid, 0, 2);
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoGenerateColumns = false;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.CellFormatting += GridCellFormatting;

            try
            {
                typeof(DataGridView).InvokeMember("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.SetProperty,
                    null, _grid, new object[] { true });
            }
            catch
            {
            }

            AddColumn("File or Directory", "Path", 520);
            AddColumn("Process", "Process", 170);
            AddColumn("Current Size", "CurrentSize", 125);
            AddColumn("Written Session", "WriteBytes", 135);
            AddColumn("Read Session", "ReadBytes", 135);
            AddColumn("Avg Write/s (5s)", "WriteRate", 125);
            AddColumn("Avg Read/s (5s)", "ReadRate", 125);
            AddColumn("Writes", "WriteOperations", 70);
            AddColumn("Reads", "ReadOperations", 70);
            AddColumn("Last Write", "LastWrite", 145);
            AddColumn("Last Read", "LastRead", 145);
        }

        private void AddColumn(string text, string property, int width)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = text,
                DataPropertyName = property,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private void WireEvents()
        {
            _startButton.Click += delegate { StartMonitor(); };
            _stopButton.Click += delegate { _monitor.Stop(); };
            _clearButton.Click += delegate { _monitor.Clear(); RefreshRows(); };
            _testButton.Click += delegate { RunSelfTest(); };
            _historyButton.Click += delegate
            {
                try
                {
                    if (!File.Exists(HistoryLogger.LogPath))
                    {
                        File.WriteAllText(HistoryLogger.LogPath, "Time\tProcess\tPID\tWritten (bytes)\tRead (bytes)\tWrite Ops\tRead Ops\tPath\r\n");
                    }
                    Process.Start(HistoryLogger.LogPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to open history log: " + ex.Message, "MonitorLarp", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            _writesCheck.CheckedChanged += delegate { ApplyCaptureSettings(); RefreshRows(); };
            _readsCheck.CheckedChanged += delegate { ApplyCaptureSettings(); RefreshRows(); };
            _bytesCheck.CheckedChanged += delegate { RefreshRows(); };
            _rootsText.TextChanged += delegate { _monitor.SetRoots(_rootsText.Text); RefreshRows(); };
            _activeCheck.CheckedChanged += delegate { RefreshRows(); };
            _activeSeconds.ValueChanged += delegate { RefreshRows(); };
            _thresholdValue.ValueChanged += delegate { RefreshRows(); };
            _thresholdUnit.SelectedIndexChanged += delegate { RefreshRows(); };
            _modeCombo.SelectedIndexChanged += delegate { RefreshRows(); };
            _groupCombo.SelectedIndexChanged += delegate { RefreshRows(); };
            _monitor.Error += ShowError;
            _monitor.RunningChanged += running =>
            {
                if (IsDisposed)
                    return;
                BeginInvoke((Action)(() => UpdateButtons(running)));
            };

            _refreshTimer.Interval = 1000;
            _refreshTimer.Tick += delegate { RefreshRows(); };
            _refreshTimer.Start();
            FormClosing += delegate
            {
                _refreshTimer.Stop();
                _monitor.Dispose();
                if (!string.IsNullOrWhiteSpace(_testFilePath))
                {
                    try { File.Delete(_testFilePath); } catch { }
                }
            };
        }

        private void StartMonitor()
        {
            _monitor.SetRoots(_rootsText.Text);
            ApplyCaptureSettings();
            _monitor.Start();
        }

        private void ApplyCaptureSettings()
        {
            _monitor.SetCapture(_writesCheck.Checked, _readsCheck.Checked);
        }

        private FilterSettings GetFilterSettings()
        {
            long multiplier = _thresholdUnit.SelectedIndex == 1 ? 1024L * 1024L * 1024L : 1024L * 1024L;
            FilterMode mode = (FilterMode)Math.Max(0, _modeCombo.SelectedIndex);
            return new FilterSettings
            {
                ShowWrites = _writesCheck.Checked,
                ShowReads = _readsCheck.Checked,
                OnlyActive = _activeCheck.Checked,
                ActiveSeconds = (int)_activeSeconds.Value,
                ThresholdBytes = (long)_thresholdValue.Value * multiplier,
                Mode = mode,
                GroupMode = (GroupMode)Math.Max(0, _groupCombo.SelectedIndex),
                Roots = ParseRoots(_rootsText.Text)
            };
        }

        private static string[] ParseRoots(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new string[0];
            return text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value =>
                {
                    string root = value.Trim();
                    if (root.Length == 2 && root[1] == ':')
                        root += "\\";
                    if (!root.EndsWith("\\", StringComparison.Ordinal))
                        root += "\\";
                    return root;
                })
                .ToArray();
        }

        private void RefreshRows()
        {
            if (IsDisposed)
                return;

            if (_isRefreshing)
            {
                _refreshPending = true;
                return;
            }

            _isRefreshing = true;
            _refreshPending = false;
            FilterSettings settings = GetFilterSettings();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<ActivityRow> rows = _monitor.GetRows(settings);
                    MonitorStatistics statistics = _monitor.GetStatistics();

                    if (IsDisposed)
                        return;

                    BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (!IsDisposed)
                            {
                                int scroll = _grid.FirstDisplayedScrollingRowIndex;
                                _grid.DataSource = rows;
                                if (scroll >= 0 && scroll < _grid.RowCount)
                                {
                                    try { _grid.FirstDisplayedScrollingRowIndex = scroll; } catch { }
                                }

                                SetByteColumnsVisible(_bytesCheck.Checked);

                                string itemWord = settings.GroupMode == GroupMode.SeparateFiles ? "files" : "groups";
                                string lastEvent = statistics.LastEvent.HasValue
                                    ? " | last event " + statistics.LastEvent.Value.ToString("HH:mm:ss")
                                    : string.Empty;

                                _status.Text = (_monitor.IsRunning ? "RUNNING" : "stopped") +
                                               " | records in RAM: " + statistics.Records +
                                               " | displayed: " + rows.Count + " " + itemWord +
                                               " | write: " + FormatBytes(statistics.WriteBytes) +
                                               " | read: " + FormatBytes(statistics.ReadBytes) + lastEvent;
                            }
                        }
                        finally
                        {
                            _isRefreshing = false;
                            if (_refreshPending)
                            {
                                _refreshPending = false;
                                RefreshRows();
                            }
                        }
                    }));
                }
                catch
                {
                    _isRefreshing = false;
                }
            });
        }

        private void SetByteColumnsVisible(bool visible)
        {
            string[] byteProperties = { "CurrentSize", "WriteBytes", "ReadBytes", "WriteRate", "ReadRate" };
            foreach (DataGridViewColumn column in _grid.Columns)
                if (byteProperties.Contains(column.DataPropertyName))
                    column.Visible = visible;
        }

        private void RunSelfTest()
        {
            if (!_monitor.IsRunning)
            {
                MessageBox.Show(this,
                    "Click 'Start' first, then run self-test.",
                    "MonitorLarp",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string path = Path.Combine(Path.GetTempPath(), "MonitorLarp-test-" + Guid.NewGuid().ToString("N") + ".bin");
            _testFilePath = path;
            _testButton.Enabled = false;
            _status.Text = "test: writing and reading temporary file...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try
                {
                    byte[] buffer = new byte[1024 * 1024];
                    new Random(42).NextBytes(buffer);
                    using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, buffer.Length, FileOptions.WriteThrough))
                    {
                        for (int i = 0; i < 16; i++)
                            output.Write(buffer, 0, buffer.Length);
                        output.Flush(true);
                    }

                    using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan))
                    {
                        while (input.Read(buffer, 0, buffer.Length) > 0) { }
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (IsDisposed)
                    return;
                BeginInvoke((Action)(() =>
                {
                    _testButton.Enabled = true;
                    if (error != null)
                        MessageBox.Show(this, "Self-test failed: " + error.Message, "MonitorLarp", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else
                        _status.Text = "self-test complete: 16 MB temporary file written and read; will be deleted on exit";
                    RefreshRows();
                }));
            });
        }

        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count)
                return;

            ActivityRow row = _grid.Rows[e.RowIndex].DataBoundItem as ActivityRow;
            if (row == null)
                return;

            string property = _grid.Columns[e.ColumnIndex].DataPropertyName;
            if (property == "CurrentSize" || property == "ReadBytes" || property == "WriteBytes" ||
                property == "ReadRate" || property == "WriteRate")
            {
                long value = 0;
                if (property == "CurrentSize")
                    value = row.CurrentSize.GetValueOrDefault();
                else if (property == "ReadBytes")
                    value = row.ReadBytes;
                else if (property == "WriteBytes")
                    value = row.WriteBytes;
                else if (property == "ReadRate")
                    value = row.ReadRate;
                else
                    value = row.WriteRate;
                e.Value = FormatBytes(value);
            }

            if (row.ActiveWrite && !row.ActiveRead)
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.LemonChiffon;
            else if (row.ActiveRead && !row.ActiveWrite)
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.Honeydew;
            else if (row.ActiveRead || row.ActiveWrite)
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.MistyRose;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L * 1024L)
                return (bytes / 1024.0).ToString("0.##") + " KB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
        }

        private void UpdateButtons(bool running)
        {
            _startButton.Enabled = !running;
            _stopButton.Enabled = running;
            _status.Text = running ? "starting ETW..." : "stopped";
        }

        private void ShowError(string message)
        {
            if (IsDisposed)
                return;
            BeginInvoke((Action)(() =>
            {
                UpdateButtons(false);
                MessageBox.Show(this,
                    "Failed to start monitoring.\r\n\r\n" + message +
                    "\r\n\r\nRun MonitorLarp as Administrator.",
                    "MonitorLarp",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }));
        }

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
