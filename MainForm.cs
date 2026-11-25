using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PlaylistViewer;

internal sealed class MainForm : Form
{
    private static readonly HttpClient HttpClient = new();
    private static readonly Font GroupRowFont = new("Segoe UI", 9F, FontStyle.Bold);
    private const string AllGroups = "(Alle Gruppen)";
    private const int RowsPerPage = 2000;
    private static readonly Regex EpisodeTitleRegex = new(
        @"^(?<series>.+?)[\s._-]+[sS](?<season>[0-9]{1,2})[\s._-]*[eE](?<episode>[0-9]{1,3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly TextBox urlTextBox = new();
    private readonly TextBox searchTextBox = new();
    private readonly ComboBox groupComboBox = new();
    private readonly CheckBox episodeGroupingCheckBox = new();
    private readonly Button downloadButton = new();
    private readonly Button saveButton = new();
    private readonly DataGridView grid = new();
    private readonly StatusStrip statusStrip = new();
    private readonly ToolStripStatusLabel statusLabel = new() { Spring = true };
    private readonly ToolStripStatusLabel sizeStatusLabel = new();
    private readonly ToolStripStatusLabel entryCountStatusLabel = new();
    private readonly ToolStripStatusLabel groupCountStatusLabel = new();
    private readonly ToolStripStatusLabel visibleCountStatusLabel = new();
    private readonly ToolStripStatusLabel lastOperationStatusLabel = new();
    private readonly ToolStripStatusLabel operationDurationStatusLabel = new();
    private readonly ToolStripButton previousPageButton = new("‹") { Enabled = false, ToolTipText = "Vorherige Seite" };
    private readonly ToolStripStatusLabel pageStatusLabel = new() { Text = "Seite 0/0" };
    private readonly ToolStripButton nextPageButton = new("›") { Enabled = false, ToolTipText = "Nächste Seite" };
    private readonly ContextMenuStrip columnMenu = new();
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 200 };
    private readonly List<M3uEntry> entries = [];
    private M3uEntry[] entrySnapshot = [];
    private List<DisplayRow> visibleRows = [];
    private EpisodeGrouping? currentEpisodeGrouping;
    private readonly HashSet<string> expandedSeries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> columnFilters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColumnValueIndex> columnValueIndexes = new(StringComparer.Ordinal);
    private Dictionary<string, int[]> groupEntryIndex = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? filterCancellation;
    private string? playlistContent;
    private long? playlistSizeBytes;
    private int? visibleEntryCount;
    private int currentPage;
    private DateTime? lastOperationAt;
    private string? lastOperationName;
    private TimeSpan? lastOperationDuration;
    private string? sortColumnName;
    private bool sortAscending = true;
    private string? groupColumnName;
    private int playlistVersion;
    private bool updatingGroups;

    private readonly record struct DisplayRow(
        M3uEntry? Entry,
        string? GroupLabel = null,
        int GroupCount = 0,
        string? NameOverride = null,
        string? EpisodeGroupKey = null,
        bool IsEpisodeGroup = false,
        bool IsExpanded = false);
    private sealed record EpisodeSeriesGroup(string Name, DisplayRow[] Episodes);
    private sealed record EpisodeGrouping(List<EpisodeSeriesGroup> Series, List<DisplayRow> UngroupedEntries);
    private sealed record FilterResult(List<DisplayRow> Rows, int EntryCount, EpisodeGrouping? EpisodeGrouping = null);
    private readonly record struct IndexedColumnValue(string Value, int EntryIndex);

    private sealed class ColumnValueIndex
    {
        private readonly IndexedColumnValue[] sortedValues;

        private ColumnValueIndex(IndexedColumnValue[] sortedValues, string[] distinctValues)
        {
            this.sortedValues = sortedValues;
            DistinctValues = distinctValues;
        }

        public string[] DistinctValues { get; }

        public static ColumnValueIndex Create(M3uEntry[] source, string columnName)
        {
            var sortedValues = new IndexedColumnValue[source.Length];
            for (var index = 0; index < source.Length; index++)
                sortedValues[index] = new IndexedColumnValue(GetColumnValue(source[index], columnName), index);

            Array.Sort(sortedValues, static (left, right) =>
            {
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Value, right.Value);
                return comparison != 0 ? comparison : left.EntryIndex.CompareTo(right.EntryIndex);
            });

            var distinctValues = new List<string>();
            for (var index = 0; index < sortedValues.Length; index++)
            {
                if (index == 0 ||
                    !StringComparer.OrdinalIgnoreCase.Equals(sortedValues[index - 1].Value, sortedValues[index].Value))
                    distinctValues.Add(sortedValues[index].Value);
            }

            return new ColumnValueIndex(sortedValues, distinctValues.ToArray());
        }

        public int CountMatches(HashSet<string> selectedValues, int limit)
        {
            var count = 0;
            foreach (var value in selectedValues)
            {
                var (start, end) = FindRange(value);
                count += end - start;
                if (count >= limit)
                    return count;
            }

            return count;
        }

        public void AddMatches(HashSet<string> selectedValues, List<int> indices, CancellationToken cancellationToken)
        {
            foreach (var value in selectedValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (start, end) = FindRange(value);
                for (var index = start; index < end; index++)
                    indices.Add(sortedValues[index].EntryIndex);
            }
        }

        private (int Start, int End) FindRange(string value)
        {
            var start = LowerBound(value);
            var low = start;
            var high = sortedValues.Length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (StringComparer.OrdinalIgnoreCase.Compare(sortedValues[middle].Value, value) <= 0)
                    low = middle + 1;
                else
                    high = middle;
            }

            var end = low;
            return (start, end);
        }

        private int LowerBound(string value)
        {
            var low = 0;
            var high = sortedValues.Length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (StringComparer.OrdinalIgnoreCase.Compare(sortedValues[middle].Value, value) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }

    public MainForm()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Text = $"Playlist Viewer v{version}";
        MinimumSize = new Size(850, 520);
        Size = new Size(1280, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;

        BuildLayout();
        LoadLastPlaylistUrl();
        FormClosed += (_, _) =>
        {
            filterCancellation?.Cancel();
            columnMenu.Dispose();
        };

        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            ApplyFilters();
        };
        searchTextBox.TextChanged += (_, _) =>
        {
            searchTimer.Stop();
            searchTimer.Start();
        };
        groupComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (!updatingGroups)
                ApplyFilters();
        };
        episodeGroupingCheckBox.CheckedChanged += (_, _) =>
        {
            expandedSeries.Clear();
            if (episodeGroupingCheckBox.Checked && groupColumnName is not null)
                groupColumnName = null;

            UpdateColumnHeaders();
            ApplyFilters(episodeGroupingCheckBox.Checked ? "Serien gruppiert" : "Gruppierung aufgehoben");
        };
        downloadButton.Click += async (_, _) => await DownloadPlaylistAsync();
        saveButton.Click += (_, _) => SavePlaylist();
        previousPageButton.Click += (_, _) => ChangePage(-1);
        nextPageButton.Click += (_, _) => ChangePage(1);
        urlTextBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await DownloadPlaylistAsync();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                searchTextBox.Focus();
                searchTextBox.SelectAll();
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedUrl();
                e.SuppressKeyPress = true;
            }
        };
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 10),
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(layout);

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label
        {
            Text = "Playlist Viewer",
            Font = new Font("Segoe UI Semibold", 18F),
            Dock = DockStyle.Fill,
            AutoSize = false,
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Text = "M3U-Playlists laden, durchsuchen und nach Gruppe filtern",
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
        }, 0, 1);
        layout.Controls.Add(heading, 0, 0);

        var urlRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0, 5, 0, 5),
        };
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        urlRow.Controls.Add(CreateLabel("Playlist-URL"), 0, 0);
        urlTextBox.Dock = DockStyle.Fill;
        urlTextBox.PlaceholderText = "https://beispiel.de/playlist.m3u";
        urlRow.Controls.Add(urlTextBox, 1, 0);
        downloadButton.Text = "Herunterladen";
        downloadButton.Dock = DockStyle.Fill;
        downloadButton.Margin = new Padding(8, 0, 0, 0);
        urlRow.Controls.Add(downloadButton, 2, 0);
        saveButton.Text = "M3U speichern";
        saveButton.Dock = DockStyle.Fill;
        saveButton.Enabled = false;
        saveButton.Margin = new Padding(8, 0, 0, 0);
        urlRow.Controls.Add(saveButton, 3, 0);
        layout.Controls.Add(urlRow, 0, 1);

        var filterRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0, 5, 0, 5),
        };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
        filterRow.Controls.Add(CreateLabel("Suche"), 0, 0);
        searchTextBox.Dock = DockStyle.Fill;
        searchTextBox.PlaceholderText = "Name, Titel, Gruppe, ID oder URL …  (Strg+F)";
        filterRow.Controls.Add(searchTextBox, 1, 0);
        filterRow.Controls.Add(CreateLabel("Gruppe"), 2, 0);
        groupComboBox.Dock = DockStyle.Fill;
        groupComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        groupComboBox.Items.Add(AllGroups);
        groupComboBox.SelectedIndex = 0;
        filterRow.Controls.Add(groupComboBox, 3, 0);
        episodeGroupingCheckBox.Text = "Serien gruppieren";
        episodeGroupingCheckBox.Dock = DockStyle.Fill;
        episodeGroupingCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        episodeGroupingCheckBox.Padding = new Padding(6, 0, 0, 0);
        filterRow.Controls.Add(episodeGroupingCheckBox, 4, 0);
        layout.Controls.Add(filterRow, 0, 2);

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowHeadersVisible = false;
        grid.AutoGenerateColumns = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = SystemColors.Window;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.VirtualMode = true;
        grid.Columns.Add(CreateColumn("Name", "Name", 1.15F));
        grid.Columns.Add(CreateColumn("Title", "Titel", 1.1F));
        grid.Columns.Add(CreateColumn("Group", "Gruppe", 0.8F));
        grid.Columns.Add(CreateColumn("Id", "TVG-ID", 0.65F));
        grid.Columns.Add(CreateColumn("Url", "Stream-URL", 2.3F));
        grid.CellValueNeeded += (_, e) =>
        {
            var displayIndex = currentPage * RowsPerPage + e.RowIndex;
            if (e.RowIndex < 0 || displayIndex >= visibleRows.Count)
                return;

            var row = visibleRows[displayIndex];
            if (row.Entry is null)
            {
                var expandIcon = row.IsEpisodeGroup ? row.IsExpanded ? "▼ " : "▶ " : string.Empty;
                e.Value = e.ColumnIndex == 0
                    ? $"{expandIcon}{row.GroupLabel}  ({row.GroupCount:N0})"
                    : string.Empty;
                return;
            }

            var entry = row.Entry;
            e.Value = e.ColumnIndex switch
            {
                0 => row.NameOverride ?? entry.Name,
                1 => entry.Title,
                2 => entry.Group,
                3 => entry.Id,
                4 => entry.Url,
                _ => string.Empty,
            };
        };
        grid.CellDoubleClick += (_, e) =>
        {
            var displayIndex = currentPage * RowsPerPage + e.RowIndex;
            if (e.RowIndex >= 0 && displayIndex < visibleRows.Count && visibleRows[displayIndex].Entry is not null)
                CopySelectedUrl();
        };
        grid.CellMouseClick += (_, e) =>
        {
            if (e.RowIndex == -1 && e.ColumnIndex >= 0 && e.Button is MouseButtons.Left or MouseButtons.Right)
                ShowColumnMenu(grid.Columns[e.ColumnIndex]);
            else if (e.RowIndex >= 0 && e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                var displayIndex = currentPage * RowsPerPage + e.RowIndex;
                if (displayIndex < visibleRows.Count &&
                    visibleRows[displayIndex] is { IsEpisodeGroup: true, EpisodeGroupKey: { } series })
                    ToggleEpisodeGroup(series);
            }
        };
        grid.CellFormatting += (_, e) =>
        {
            var displayIndex = currentPage * RowsPerPage + e.RowIndex;
            if (e.RowIndex < 0 || displayIndex >= visibleRows.Count || visibleRows[displayIndex].Entry is not null)
                return;

            e.CellStyle.BackColor = Color.FromArgb(235, 239, 244);
            e.CellStyle.ForeColor = Color.FromArgb(35, 48, 65);
            e.CellStyle.Font = GroupRowFont;
        };
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Stream-URL kopieren", null, (_, _) => CopySelectedUrl());
        grid.ContextMenuStrip = contextMenu;
        layout.Controls.Add(grid, 0, 3);

        statusLabel.Text = "Playlist-URL eingeben und auf „Herunterladen“ klicken.";
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        sizeStatusLabel.Overflow = ToolStripItemOverflow.Never;
        entryCountStatusLabel.Overflow = ToolStripItemOverflow.Never;
        groupCountStatusLabel.Overflow = ToolStripItemOverflow.Never;
        visibleCountStatusLabel.Overflow = ToolStripItemOverflow.Never;
        lastOperationStatusLabel.Overflow = ToolStripItemOverflow.Never;
        operationDurationStatusLabel.Overflow = ToolStripItemOverflow.Never;
        statusStrip.Dock = DockStyle.Fill;
        statusStrip.SizingGrip = false;
        statusStrip.Items.AddRange([
            statusLabel,
            sizeStatusLabel,
            entryCountStatusLabel,
            groupCountStatusLabel,
            visibleCountStatusLabel,
            previousPageButton,
            pageStatusLabel,
            nextPageButton,
            lastOperationStatusLabel,
            operationDurationStatusLabel,
        ]);
        layout.Controls.Add(statusStrip, 0, 4);
        UpdateStatusMetrics();
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private void MarkOperationCompleted(string operationName, TimeSpan duration)
    {
        lastOperationName = operationName;
        lastOperationAt = DateTime.Now;
        lastOperationDuration = duration;
        UpdateStatusMetrics();
    }

    private void ChangePage(int direction)
    {
        var pageCount = (visibleRows.Count + RowsPerPage - 1) / RowsPerPage;
        var nextPage = currentPage + direction;
        if (nextPage < 0 || nextPage >= pageCount)
            return;

        var startedAt = Stopwatch.GetTimestamp();
        currentPage = nextPage;
        UpdateGridPage();
        MarkOperationCompleted("Seite gewechselt", Stopwatch.GetElapsedTime(startedAt));
    }

    private void UpdateGridPage()
    {
        var pageCount = (visibleRows.Count + RowsPerPage - 1) / RowsPerPage;
        currentPage = pageCount == 0 ? 0 : Math.Min(currentPage, pageCount - 1);
        var pageOffset = currentPage * RowsPerPage;
        var pageRowCount = Math.Min(RowsPerPage, Math.Max(0, visibleRows.Count - pageOffset));
        if (grid.RowCount != pageRowCount)
            grid.RowCount = pageRowCount;

        pageStatusLabel.Text = pageCount == 0
            ? "Seite 0/0"
            : $"Seite {currentPage + 1:N0}/{pageCount:N0}";
        previousPageButton.Enabled = currentPage > 0;
        nextPageButton.Enabled = currentPage + 1 < pageCount;
        grid.Invalidate();
    }

    private void UpdateStatusMetrics()
    {
        sizeStatusLabel.Text = playlistSizeBytes is { } bytes
            ? $"M3U: {FormatSize(bytes)}"
            : "M3U: —";
        entryCountStatusLabel.Text = playlistContent is null
            ? "Einträge: —"
            : $"Einträge: {entries.Count:N0}";
        groupCountStatusLabel.Text = playlistContent is null
            ? "Gruppen: —"
            : $"Gruppen: {Math.Max(0, groupComboBox.Items.Count - 1):N0}";
        visibleCountStatusLabel.Text = visibleEntryCount is { } visible
            ? $"Sichtbar: {visible:N0}"
            : "Sichtbar: —";
        lastOperationStatusLabel.Text = lastOperationAt is { } time
            ? $"Letzte Aktion: {lastOperationName} ({time.ToString("g", System.Globalization.CultureInfo.CurrentCulture)})"
            : "Letzte Aktion: —";
        operationDurationStatusLabel.Text = lastOperationDuration is { } duration
            ? $"Dauer: {FormatDuration(duration)}"
            : "Dauer: —";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:N0} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s"
            : $"{duration.TotalSeconds:0.00} s";

    private static DataGridViewTextBoxColumn CreateColumn(string name, string header, float fillWeight) => new()
    {
        Name = name,
        HeaderText = header,
        FillWeight = fillWeight,
        SortMode = DataGridViewColumnSortMode.Programmatic,
        MinimumWidth = 75,
    };

    private void ShowColumnMenu(DataGridViewColumn column)
    {
        if (columnMenu.Visible)
            return;

        columnMenu.Items.Clear();
        var menu = columnMenu;
        menu.Items.Add("Aufsteigend sortieren (A bis Z)", null, (_, _) => SetSort(column.Name, ascending: true));
        menu.Items.Add("Absteigend sortieren (Z bis A)", null, (_, _) => SetSort(column.Name, ascending: false));
        menu.Items.Add(new ToolStripSeparator());

        var groupItem = new ToolStripMenuItem(
            groupColumnName == column.Name ? "Gruppierung aufheben" : "Nach dieser Spalte gruppieren");
        groupItem.Click += (_, _) =>
        {
            groupColumnName = groupColumnName == column.Name ? null : column.Name;
            if (groupColumnName is not null && episodeGroupingCheckBox.Checked)
                episodeGroupingCheckBox.Checked = false;
            else
            {
                UpdateColumnHeaders();
                ApplyFilters();
            }
        };
        menu.Items.Add(groupItem);
        menu.Items.Add(new ToolStripSeparator());

        var filterItem = new ToolStripMenuItem("Nach Werten filtern …");
        filterItem.Click += async (_, _) => await ShowValueFilterAsync(column);
        menu.Items.Add(filterItem);

        var clearFilterItem = new ToolStripMenuItem("Filter für diese Spalte löschen")
        {
            Enabled = columnFilters.ContainsKey(column.Name),
        };
        clearFilterItem.Click += (_, _) =>
        {
            columnFilters.Remove(column.Name);
            UpdateColumnHeaders();
            ApplyFilters();
        };
        menu.Items.Add(clearFilterItem);
        var headerBounds = grid.GetCellDisplayRectangle(column.Index, -1, false);
        menu.Show(grid, new Point(headerBounds.Left, headerBounds.Bottom));
    }

    private void SetSort(string columnName, bool ascending)
    {
        sortColumnName = columnName;
        sortAscending = ascending;
        UpdateColumnHeaders();
        ApplyFilters();
    }

    private async Task ShowValueFilterAsync(DataGridViewColumn column)
    {
        var version = playlistVersion;
        IReadOnlyList<string> values;
        if (column.Name == "Group")
        {
            values = groupEntryIndex.Keys
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            if (!columnValueIndexes.TryGetValue(column.Name, out var valueIndex))
            {
                var source = entrySnapshot;
                statusLabel.Text = "Filterwerte werden vorbereitet …";
                valueIndex = await Task.Run(() => ColumnValueIndex.Create(source, column.Name));
                if (IsDisposed || version != playlistVersion)
                    return;
                columnValueIndexes[column.Name] = valueIndex;
            }

            values = valueIndex.DistinctValues;
        }

        if (IsDisposed || version != playlistVersion)
            return;

        columnFilters.TryGetValue(column.Name, out var selectedValues);

        using var dialog = new ValueFilterForm(column.HeaderText, values, selectedValues);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedValues is null)
        {
            ApplyFilters();
            return;
        }

        if (dialog.SelectedValues.Count == values.Count)
            columnFilters.Remove(column.Name);
        else
            columnFilters[column.Name] = dialog.SelectedValues;

        UpdateColumnHeaders();
        ApplyFilters();
    }

    private void UpdateColumnHeaders()
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            var activeFilter = columnFilters.ContainsKey(column.Name) ? "  (Filter)" : string.Empty;
            var grouped = groupColumnName == column.Name ? "  (Gruppiert)" : string.Empty;
            column.HeaderCell.Value = $"{column.HeaderText}  ▾{activeFilter}{grouped}";
            column.HeaderCell.SortGlyphDirection = sortColumnName == column.Name
                ? sortAscending ? SortOrder.Ascending : SortOrder.Descending
                : SortOrder.None;
        }
    }

    private static string GetColumnValue(M3uEntry entry, string columnName) => columnName switch
    {
        "Name" => entry.Name,
        "Title" => entry.Title,
        "Group" => entry.Group,
        "Id" => entry.Id,
        "Url" => entry.Url,
        _ => string.Empty,
    };

    private async Task DownloadPlaylistAsync()
    {
        var operationStartedAt = Stopwatch.GetTimestamp();
        var address = urlTextBox.Text.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MarkOperationCompleted("Ungültige URL", Stopwatch.GetElapsedTime(operationStartedAt));
            MessageBox.Show(this, "Bitte gib eine gültige HTTP- oder HTTPS-URL ein.", "Ungültige URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            urlTextBox.Focus();
            return;
        }

        downloadButton.Enabled = false;
        statusLabel.Text = "Playlist wird heruntergeladen …";
        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var downloadedContent = await response.Content.ReadAsStringAsync();
            var parsedEntries = M3uParser.Parse(downloadedContent);

            playlistContent = downloadedContent;
            playlistSizeBytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(downloadedContent);
            entries.Clear();
            entries.AddRange(parsedEntries);
            entrySnapshot = parsedEntries.ToArray();
            groupEntryIndex = BuildGroupEntryIndex(entrySnapshot);
            columnValueIndexes.Clear();
            expandedSeries.Clear();
            visibleEntryCount = null;
            playlistVersion++;
            columnFilters.Clear();
            sortColumnName = null;
            sortAscending = true;
            groupColumnName = null;
            saveButton.Enabled = true;
            PopulateGroups();
            UpdateColumnHeaders();
            statusLabel.Text = "Download abgeschlossen; Filter wird aktualisiert …";
            ApplyFilters("Download", operationStartedAt);
            if (TrySaveLastPlaylistUrl(address, out var saveError))
            {
                UpdateStatusMetrics();
            }
            else
            {
                statusLabel.Text = "Playlist geladen, URL konnte nicht gespeichert werden.";
                MessageBox.Show(this, $"Die Playlist wurde geladen, aber die URL konnte nicht gespeichert werden.\n\n{saveError}",
                    "URL konnte nicht gespeichert werden", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                MarkOperationCompleted("Download", Stopwatch.GetElapsedTime(operationStartedAt));
            }
        }
        catch (HttpRequestException ex)
        {
            statusLabel.Text = "Download fehlgeschlagen.";
            MarkOperationCompleted("Download fehlgeschlagen", Stopwatch.GetElapsedTime(operationStartedAt));
            MessageBox.Show(this, $"Die Playlist konnte nicht heruntergeladen werden.\n\n{ex.Message}",
                "Download fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (TaskCanceledException ex)
        {
            statusLabel.Text = "Download abgebrochen oder Zeitüberschreitung.";
            MarkOperationCompleted("Download abgebrochen", Stopwatch.GetElapsedTime(operationStartedAt));
            MessageBox.Show(this, $"Der Download wurde abgebrochen oder hat zu lange gedauert.\n\n{ex.Message}",
                "Download fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (IOException ex)
        {
            statusLabel.Text = "Playlist konnte nicht verarbeitet werden.";
            MarkOperationCompleted("Lesefehler", Stopwatch.GetElapsedTime(operationStartedAt));
            MessageBox.Show(this, $"Die Playlist konnte nicht gelesen werden.\n\n{ex.Message}",
                "Lesefehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            downloadButton.Enabled = true;
        }
    }

    private static string LastPlaylistUrlPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlaylistViewer",
        "last-playlist-url.txt");

    private void LoadLastPlaylistUrl()
    {
        try
        {
            if (File.Exists(LastPlaylistUrlPath))
                urlTextBox.Text = File.ReadAllText(LastPlaylistUrlPath, Encoding.UTF8).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Die zuletzt verwendete Playlist-URL konnte nicht geladen werden.\n\n{ex.Message}",
                "Gespeicherte URL nicht verfügbar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool TrySaveLastPlaylistUrl(string address, out string? error)
    {
        try
        {
            var path = LastPlaylistUrlPath;
            var directory = Path.GetDirectoryName(path);
            if (directory is null)
                throw new IOException("Der Speicherpfad für die Playlist-URL ist ungültig.");

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, address, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private void PopulateGroups()
    {
        updatingGroups = true;
        groupComboBox.BeginUpdate();
        groupComboBox.Items.Clear();
        groupComboBox.Items.Add(AllGroups);
        foreach (var group in entries
                     .Select(entry => entry.Group)
                     .Where(group => !string.IsNullOrWhiteSpace(group))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group, StringComparer.OrdinalIgnoreCase))
        {
            groupComboBox.Items.Add(group);
        }
        groupComboBox.SelectedIndex = 0;
        groupComboBox.EndUpdate();
        updatingGroups = false;
    }

    private static Dictionary<string, int[]> BuildGroupEntryIndex(M3uEntry[] source)
    {
        var mutableIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < source.Length; index++)
        {
            var group = source[index].Group;
            if (!mutableIndex.TryGetValue(group, out var groupEntries))
            {
                groupEntries = [];
                mutableIndex.Add(group, groupEntries);
            }
            groupEntries.Add(index);
        }

        return mutableIndex.ToDictionary(
            item => item.Key,
            item => item.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async void ApplyFilters(string operationName = "Filter", long? operationStartedAt = null)
    {
        var filterStartedAt = operationStartedAt ?? Stopwatch.GetTimestamp();
        filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        filterCancellation = cancellation;

        var source = entrySnapshot;
        var filters = columnFilters
            .Select(filter => new KeyValuePair<string, HashSet<string>>(
                filter.Key,
                new HashSet<string>(filter.Value, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var indexes = new Dictionary<string, ColumnValueIndex>(columnValueIndexes, StringComparer.Ordinal);
        var groups = groupEntryIndex;
        var query = searchTextBox.Text.Trim();
        var selectedGroup = groupComboBox.SelectedItem as string ?? AllGroups;
        var activeSortColumn = sortColumnName;
        var ascending = sortAscending;
        var activeGroupColumn = groupColumnName;
        var groupEpisodes = episodeGroupingCheckBox.Checked;
        var expanded = new HashSet<string>(expandedSeries, StringComparer.OrdinalIgnoreCase);

        statusLabel.Text = "Filter wird angewendet …";
        try
        {
            var result = await Task.Run(
                () => BuildFilteredRows(source, filters, indexes, groups, query, selectedGroup, activeSortColumn, ascending, activeGroupColumn, groupEpisodes, expanded, cancellation.Token),
                cancellation.Token);

            if (cancellation.IsCancellationRequested || IsDisposed || !ReferenceEquals(filterCancellation, cancellation))
                return;

            visibleRows = result.Rows;
            currentEpisodeGrouping = result.EpisodeGrouping;
            visibleEntryCount = result.EntryCount;
            currentPage = 0;
            UpdateGridPage();
            statusLabel.Text = "Bereit.";
            MarkOperationCompleted(operationName, Stopwatch.GetElapsedTime(filterStartedAt));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(filterCancellation, cancellation))
                filterCancellation = null;
            cancellation.Dispose();
        }
    }

    private static FilterResult BuildFilteredRows(
        M3uEntry[] source,
        KeyValuePair<string, HashSet<string>>[] filters,
        IReadOnlyDictionary<string, ColumnValueIndex> indexes,
        IReadOnlyDictionary<string, int[]> groupIndex,
        string query,
        string selectedGroup,
        string? sortColumn,
        bool ascending,
        string? groupColumn,
        bool groupEpisodes,
        HashSet<string> expandedSeries,
        CancellationToken cancellationToken)
    {
        var candidates = GetIndexedCandidates(source.Length, filters, indexes, groupIndex, cancellationToken);
        List<int> matchingIndices;
        if (candidates is null)
        {
            matchingIndices = new List<int>(source.Length);
            for (var index = 0; index < source.Length; index++)
            {
                if ((index & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                if (MatchesEntry(source[index], filters, query, selectedGroup))
                    matchingIndices.Add(index);
            }
        }
        else
        {
            candidates.Sort();
            var writeIndex = 0;
            for (var readIndex = 0; readIndex < candidates.Count; readIndex++)
            {
                if ((readIndex & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var entryIndex = candidates[readIndex];
                if (MatchesEntry(source[entryIndex], filters, query, selectedGroup))
                    candidates[writeIndex++] = entryIndex;
            }

            if (writeIndex < candidates.Count)
                candidates.RemoveRange(writeIndex, candidates.Count - writeIndex);
            matchingIndices = candidates;
        }

        var entryCount = matchingIndices.Count;
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (groupColumn is null)
        {
            SortIndices(matchingIndices, source, sortColumn, ascending, comparer);
            if (groupEpisodes)
            {
                var episodeGrouping = BuildEpisodeGroups(source, matchingIndices, cancellationToken);
                var episodeRows = BuildEpisodeRows(episodeGrouping, expandedSeries);
                return new FilterResult(episodeRows, entryCount, episodeGrouping);
            }

            var rows = new List<DisplayRow>(matchingIndices.Count);
            foreach (var index in matchingIndices)
                rows.Add(new DisplayRow(source[index]));
            return new FilterResult(rows, entryCount);
        }

        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in matchingIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetColumnValue(source[index], groupColumn);
            if (!groups.TryGetValue(key, out var groupEntries))
            {
                groupEntries = [];
                groups.Add(key, groupEntries);
            }
            groupEntries.Add(index);
        }

        var groupKeys = groups.Keys.ToList();
        groupKeys.Sort((left, right) =>
        {
            var result = comparer.Compare(left, right);
            return sortColumn == groupColumn && !ascending ? -result : result;
        });

        var groupedRows = new List<DisplayRow>(entryCount + groups.Count);
        foreach (var key in groupKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indices = groups[key];
            if (sortColumn is not null && sortColumn != groupColumn)
                SortIndices(indices, source, sortColumn, ascending, comparer);

            groupedRows.Add(new DisplayRow(null, string.IsNullOrEmpty(key) ? "(Leer)" : key, indices.Count));
            foreach (var index in indices)
                groupedRows.Add(new DisplayRow(source[index]));
        }

        return new FilterResult(groupedRows, entryCount);
    }

    private static EpisodeGrouping BuildEpisodeGroups(
        M3uEntry[] source,
        List<int> entryIndices,
        CancellationToken cancellationToken)
    {
        var seriesGroups = new Dictionary<string, List<(int EntryIndex, int Season, int Episode)>>(
            StringComparer.OrdinalIgnoreCase);
        var ungroupedEntries = new List<int>();

        foreach (var entryIndex in entryIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = EpisodeTitleRegex.Match(source[entryIndex].Name.Trim());
            if (!match.Success)
            {
                ungroupedEntries.Add(entryIndex);
                continue;
            }

            var series = match.Groups["series"].Value.Trim();
            if (series.Length == 0)
            {
                ungroupedEntries.Add(entryIndex);
                continue;
            }

            if (!seriesGroups.TryGetValue(series, out var episodes))
            {
                episodes = [];
                seriesGroups.Add(series, episodes);
            }

            episodes.Add((
                entryIndex,
                int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        var seriesNames = seriesGroups.Keys.ToList();
        seriesNames.Sort(StringComparer.OrdinalIgnoreCase);
        var groupedSeries = new List<EpisodeSeriesGroup>(seriesNames.Count);
        foreach (var series in seriesNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodes = seriesGroups[series];
            episodes.Sort(static (left, right) =>
            {
                var seasonComparison = left.Season.CompareTo(right.Season);
                if (seasonComparison != 0)
                    return seasonComparison;

                var episodeComparison = left.Episode.CompareTo(right.Episode);
                return episodeComparison != 0 ? episodeComparison : left.EntryIndex.CompareTo(right.EntryIndex);
            });

            var episodeRows = new DisplayRow[episodes.Count];
            for (var index = 0; index < episodes.Count; index++)
            {
                var episode = episodes[index];
                episodeRows[index] = new DisplayRow(
                    source[episode.EntryIndex],
                    NameOverride: $"S{episode.Season:00} E{episode.Episode:00}");
            }

            groupedSeries.Add(new EpisodeSeriesGroup(series, episodeRows));
        }

        var unmatchedRows = new List<DisplayRow>(ungroupedEntries.Count);
        foreach (var entryIndex in ungroupedEntries)
            unmatchedRows.Add(new DisplayRow(source[entryIndex]));

        return new EpisodeGrouping(groupedSeries, unmatchedRows);
    }

    private static List<DisplayRow> BuildEpisodeRows(
        EpisodeGrouping grouping,
        HashSet<string> expandedSeries)
    {
        var rowCount = grouping.Series.Count + grouping.UngroupedEntries.Count;
        foreach (var series in grouping.Series)
        {
            if (expandedSeries.Contains(series.Name))
                rowCount += series.Episodes.Length;
        }

        var rows = new List<DisplayRow>(rowCount);
        foreach (var series in grouping.Series)
        {
            var expanded = expandedSeries.Contains(series.Name);
            rows.Add(new DisplayRow(
                null,
                series.Name,
                series.Episodes.Length,
                EpisodeGroupKey: series.Name,
                IsEpisodeGroup: true,
                IsExpanded: expanded));

            if (expanded)
                rows.AddRange(series.Episodes);
        }

        rows.AddRange(grouping.UngroupedEntries);
        return rows;
    }

    private void ToggleEpisodeGroup(string series)
    {
        if (currentEpisodeGrouping is null)
            return;

        var startedAt = Stopwatch.GetTimestamp();
        if (!expandedSeries.Add(series))
            expandedSeries.Remove(series);

        visibleRows = BuildEpisodeRows(currentEpisodeGrouping, expandedSeries);
        currentPage = 0;
        UpdateGridPage();
        MarkOperationCompleted(
            expandedSeries.Contains(series) ? "Serie erweitert" : "Serie minimiert",
            Stopwatch.GetElapsedTime(startedAt));
    }

    private static List<int>? GetIndexedCandidates(
        int sourceCount,
        KeyValuePair<string, HashSet<string>>[] filters,
        IReadOnlyDictionary<string, ColumnValueIndex> indexes,
        IReadOnlyDictionary<string, int[]> groupIndex,
        CancellationToken cancellationToken)
    {
        ColumnValueIndex? bestIndex = null;
        IReadOnlyDictionary<string, int[]>? bestGroupIndex = null;
        HashSet<string>? bestValues = null;
        var bestCount = sourceCount;

        foreach (var filter in filters)
        {
            if (filter.Value.Count == 0)
                return [];
            if (filter.Value.Count > 1024 ||
                (filter.Key != "Group" && !indexes.ContainsKey(filter.Key)))
                continue;

            var count = filter.Key == "Group"
                ? CountGroupMatches(filter.Value, groupIndex, sourceCount)
                : indexes[filter.Key].CountMatches(filter.Value, sourceCount);
            if (count < bestCount)
            {
                bestCount = count;
                bestIndex = filter.Key == "Group" ? null : indexes[filter.Key];
                bestGroupIndex = filter.Key == "Group" ? groupIndex : null;
                bestValues = filter.Value;
            }
        }

        if (bestCount == 0)
            return [];
        if ((bestIndex is null && bestGroupIndex is null) || bestValues is null || bestCount > sourceCount / 4)
            return null;

        var candidates = new List<int>(bestCount);
        if (bestGroupIndex is not null)
        {
            foreach (var value in bestValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bestGroupIndex.TryGetValue(value, out var indices))
                    candidates.AddRange(indices);
            }
        }
        else
        {
            bestIndex!.AddMatches(bestValues, candidates, cancellationToken);
        }

        return candidates;
    }

    private static int CountGroupMatches(
        HashSet<string> selectedValues,
        IReadOnlyDictionary<string, int[]> groupIndex,
        int limit)
    {
        var count = 0;
        foreach (var value in selectedValues)
        {
            if (groupIndex.TryGetValue(value, out var indices))
                count += indices.Length;
            if (count >= limit)
                return count;
        }

        return count;
    }

    private static bool MatchesEntry(
        M3uEntry entry,
        KeyValuePair<string, HashSet<string>>[] filters,
        string query,
        string selectedGroup)
    {
        if (selectedGroup != AllGroups &&
            !string.Equals(entry.Group, selectedGroup, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.Length > 0 && !entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var filter in filters)
        {
            if (!filter.Value.Contains(GetColumnValue(entry, filter.Key)))
                return false;
        }

        return true;
    }

    private static void SortIndices(
        List<int> indices,
        M3uEntry[] source,
        string? sortColumn,
        bool ascending,
        StringComparer comparer)
    {
        if (sortColumn is null)
            return;

        indices.Sort((left, right) =>
        {
            var result = comparer.Compare(
                GetColumnValue(source[left], sortColumn),
                GetColumnValue(source[right], sortColumn));
            if (result == 0)
                return left.CompareTo(right);
            return ascending ? result : result < 0 ? 1 : -1;
        });
    }

    private void SavePlaylist()
    {
        if (playlistContent is null)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "M3U-Playlist speichern",
            Filter = "M3U-Playlist (*.m3u;*.m3u8)|*.m3u;*.m3u8|Alle Dateien (*.*)|*.*",
            FileName = "playlist.m3u",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var operationStartedAt = Stopwatch.GetTimestamp();
        try
        {
            File.WriteAllText(dialog.FileName, playlistContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            statusLabel.Text = $"Playlist gespeichert: {dialog.FileName}";
            MarkOperationCompleted("M3U gespeichert", Stopwatch.GetElapsedTime(operationStartedAt));
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Die Playlist konnte nicht gespeichert werden.\n\n{ex.Message}",
                "Speicherfehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            MarkOperationCompleted("Speichern fehlgeschlagen", Stopwatch.GetElapsedTime(operationStartedAt));
        }
    }

    private void CopySelectedUrl()
    {
        if (grid.CurrentRow is null || grid.CurrentRow.Index < 0)
            return;

        var displayIndex = currentPage * RowsPerPage + grid.CurrentRow.Index;
        if (displayIndex >= visibleRows.Count || visibleRows[displayIndex].Entry is not { } entry)
            return;

        Clipboard.SetText(entry.Url);
        statusLabel.Text = "Stream-URL in die Zwischenablage kopiert.";
        MarkOperationCompleted("URL kopiert", TimeSpan.Zero);
    }

    private sealed class ValueFilterForm : Form
    {
        private readonly IReadOnlyList<string> values;
        private readonly HashSet<string> selectedValues;
        private readonly TextBox searchBox = new();
        private readonly DataGridView valuesGrid = new();
        private IReadOnlyList<string> visibleValues = Array.Empty<string>();

        public HashSet<string>? SelectedValues { get; private set; }

        public ValueFilterForm(string columnName, IReadOnlyList<string> values, HashSet<string>? selectedValues)
        {
            this.values = values;
            this.selectedValues = selectedValues is null
                ? new HashSet<string>(values, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(selectedValues, StringComparer.OrdinalIgnoreCase);

            Text = $"Nach Werten filtern – {columnName}";
            Size = new Size(360, 480);
            MinimumSize = new Size(300, 360);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 4,
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(layout);

            searchBox.Dock = DockStyle.Fill;
            searchBox.PlaceholderText = "Werte suchen …";
            searchBox.TextChanged += (_, _) => RefreshValuesList();
            layout.Controls.Add(searchBox, 0, 0);

            valuesGrid.Dock = DockStyle.Fill;
            valuesGrid.ReadOnly = false;
            valuesGrid.AllowUserToAddRows = false;
            valuesGrid.AllowUserToDeleteRows = false;
            valuesGrid.AllowUserToResizeRows = false;
            valuesGrid.RowHeadersVisible = false;
            valuesGrid.ColumnHeadersVisible = false;
            valuesGrid.MultiSelect = false;
            valuesGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            valuesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            valuesGrid.BackgroundColor = SystemColors.Window;
            valuesGrid.BorderStyle = BorderStyle.FixedSingle;
            valuesGrid.RowTemplate.Height = 24;
            valuesGrid.VirtualMode = true;
            valuesGrid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Selected",
                FillWeight = 12,
                MinimumWidth = 34,
                ReadOnly = false,
                ThreeState = false,
            });
            valuesGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Value",
                FillWeight = 88,
                MinimumWidth = 180,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            valuesGrid.CellValueNeeded += (_, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= visibleValues.Count)
                    return;

                var value = visibleValues[e.RowIndex];
                e.Value = e.ColumnIndex == 0
                    ? this.selectedValues.Contains(value)
                    : string.IsNullOrEmpty(value) ? "(Leer)" : value;
            };
            valuesGrid.CellValuePushed += (_, e) =>
            {
                if (e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= visibleValues.Count)
                    return;

                var value = visibleValues[e.RowIndex];
                if (e.Value is true)
                    this.selectedValues.Add(value);
                else
                    this.selectedValues.Remove(value);
            };
            valuesGrid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (valuesGrid.IsCurrentCellDirty)
                    valuesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            layout.Controls.Add(valuesGrid, 0, 1);

            var selectionButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
            };
            var selectAllButton = new Button { Text = "Alle auswählen", AutoSize = true };
            selectAllButton.Click += (_, _) => SetVisibleValuesChecked(true);
            var selectNoneButton = new Button { Text = "Keine auswählen", AutoSize = true };
            selectNoneButton.Click += (_, _) => SetVisibleValuesChecked(false);
            selectionButtons.Controls.Add(selectAllButton);
            selectionButtons.Controls.Add(selectNoneButton);
            layout.Controls.Add(selectionButtons, 0, 2);

            var dialogButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
            };
            var okButton = new Button { Text = "OK", AutoSize = true };
            okButton.Click += (_, _) =>
            {
                SelectedValues = new HashSet<string>(this.selectedValues, StringComparer.OrdinalIgnoreCase);
                DialogResult = DialogResult.OK;
                Close();
            };
            var cancelButton = new Button { Text = "Abbrechen", AutoSize = true, DialogResult = DialogResult.Cancel };
            dialogButtons.Controls.Add(okButton);
            dialogButtons.Controls.Add(cancelButton);
            layout.Controls.Add(dialogButtons, 0, 3);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            RefreshValuesList();
        }

        private void RefreshValuesList()
        {
            var query = searchBox.Text.Trim();
            visibleValues = query.Length == 0
                ? values
                : values.Where(value =>
                        (string.IsNullOrEmpty(value) ? "(Leer)" : value)
                        .Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            valuesGrid.RowCount = visibleValues.Count;
            valuesGrid.Invalidate();
        }

        private void SetVisibleValuesChecked(bool isChecked)
        {
            foreach (var value in visibleValues)
            {
                if (isChecked)
                    selectedValues.Add(value);
                else
                    selectedValues.Remove(value);
            }
            valuesGrid.Invalidate();
        }
    }
}
