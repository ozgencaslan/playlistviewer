using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using MauiRoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace PlaylistViewer.Android;

public sealed class MainPage : ContentPage
{
    private const string LocalServerHost = "192.168.0.47";
    private const string LocalServerCertificateSha256 = "AB08D132D6BF0A40D4640692478AC06116C67A763868FA2E9FD2609586D30F2E";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex EpisodeTitleRegex = new(
        @"^(?<series>.+?)[\s._-]+[sS](?<season>[0-9]{1,2})[\s._-]*[eE](?<episode>[0-9]{1,3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string AllGroups = AppStrings.Get("GroupAll");
    private const string LastPlaylistUrlKey = "last-playlist-url";
    private static readonly string[] ColumnNames = ["Name", "Title", "Group", "Id", "Url"];
    private static readonly SortOption[] SortOptions =
    [
        new(AppStrings.Get("SortOriginal"), null, true),
        new(AppStrings.Get("SortNameAscending"), "Name", true),
        new(AppStrings.Get("SortNameDescending"), "Name", false),
        new(AppStrings.Get("SortTitleAscending"), "Title", true),
        new(AppStrings.Get("SortTitleDescending"), "Title", false),
        new(AppStrings.Get("SortGroupAscending"), "Group", true),
        new(AppStrings.Get("SortGroupDescending"), "Group", false),
        new(AppStrings.Get("SortIdAscending"), "Id", true),
        new(AppStrings.Get("SortIdDescending"), "Id", false),
        new(AppStrings.Get("SortUrlAscending"), "Url", true),
        new(AppStrings.Get("SortUrlDescending"), "Url", false),
    ];

    private static HttpClient CreateHttpClient()
    {
#if ANDROID
        var handler = new Xamarin.Android.Net.AndroidMessageHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None)
                    return true;

                if (!string.Equals(message?.RequestUri?.Host, LocalServerHost, StringComparison.Ordinal)
                    || cert is null)
                    return false;

                var certificateHash = SHA256.HashData(cert.GetRawCertData());
                return CryptographicOperations.FixedTimeEquals(
                    certificateHash,
                    Convert.FromHexString(LocalServerCertificateSha256));
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
#else
        return new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
#endif
    }

    private readonly Entry urlEntry = new()
    {
        Placeholder = AppStrings.Get("UrlPlaceholder"),
        Keyboard = Keyboard.Url,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
    };
    private readonly Entry searchEntry = new()
    {
        Placeholder = AppStrings.Get("SearchPlaceholder"),
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
    };
    private readonly Picker groupPicker = new() { Title = AppStrings.Get("ChooseGroup") };
    private readonly Picker groupColumnPicker = new() { Title = AppStrings.Get("GroupBy") };
    private readonly Picker sortPicker = new() { Title = AppStrings.Get("SortBy") };
    private readonly Picker filterColumnPicker = new() { Title = AppStrings.Get("FilterByValue") };
    private readonly Microsoft.Maui.Controls.Switch episodeGroupingSwitch = new();
    private readonly Button downloadButton = new() { Text = AppStrings.Get("Download") };
    private readonly Button shareButton = new() { Text = AppStrings.Get("SharePlaylist"), IsEnabled = false };
    private readonly Button valueFilterButton = new() { Text = AppStrings.Get("ChooseValues") };
    private readonly Label statusLabel = new()
    {
        Text = AppStrings.Get("InitialStatus"),
        FontSize = 12,
        TextColor = Colors.DimGray,
    };
    private readonly CollectionView playlistView = new()
    {
        SelectionMode = SelectionMode.None,
        ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
        ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical)
        {
            ItemSpacing = 2,
        },
    };
    private readonly ObservableCollection<PlaylistRow> visibleRows = [];
    private readonly List<M3uEntry> entries = [];
    private readonly HashSet<string> expandedSeries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> columnFilters = new(StringComparer.Ordinal);
    private CancellationTokenSource? filterCancellation;
    private string? playlistContent;
    private string? playlistAddress;
    private string? groupColumnName;
    private SortOption sortOption = SortOptions[0];
    private bool updatingGroups;

    public MainPage()
    {
        Title = AppStrings.Get("AppTitle");
        Padding = new Thickness(14, 8);

        urlEntry.Text = Preferences.Get(LastPlaylistUrlKey, string.Empty);
        groupPicker.Items.Add(AllGroups);
        groupPicker.SelectedIndex = 0;
        groupColumnPicker.Items.Add(AppStrings.Get("NoGrouping"));
        filterColumnPicker.Items.Add(AppStrings.Get("ChooseColumn"));
        foreach (var columnName in ColumnNames)
        {
            var label = GetColumnLabel(columnName);
            groupColumnPicker.Items.Add(label);
            filterColumnPicker.Items.Add(label);
        }
        groupColumnPicker.SelectedIndex = 0;
        foreach (var option in SortOptions)
            sortPicker.Items.Add(option.Label);
        sortPicker.SelectedIndex = 0;
        filterColumnPicker.SelectedIndex = 0;
        playlistView.ItemsSource = visibleRows;
        playlistView.ItemTemplate = new DataTemplate(CreatePlaylistRow);

        downloadButton.Clicked += async (_, _) => await DownloadPlaylistAsync();
        shareButton.Clicked += async (_, _) => await SharePlaylistAsync();
        searchEntry.TextChanged += (_, _) => ScheduleFiltering();
        groupPicker.SelectedIndexChanged += (_, _) =>
        {
            if (updatingGroups)
                return;

            searchEntry.Text = string.Empty;
            columnFilters.Clear();
            UpdateFilterButtonText();
            ScheduleFiltering();
        };
        episodeGroupingSwitch.Toggled += (_, _) =>
        {
            expandedSeries.Clear();
            if (episodeGroupingSwitch.IsToggled && groupColumnPicker.SelectedIndex != 0)
                groupColumnPicker.SelectedIndex = 0;
            ScheduleFiltering();
        };
        groupColumnPicker.SelectedIndexChanged += (_, _) =>
        {
            groupColumnName = groupColumnPicker.SelectedIndex <= 0
                ? null
                : ColumnNames[groupColumnPicker.SelectedIndex - 1];
            if (groupColumnName is not null && episodeGroupingSwitch.IsToggled)
                episodeGroupingSwitch.IsToggled = false;
            ScheduleFiltering();
        };
        sortPicker.SelectedIndexChanged += (_, _) =>
        {
            var index = sortPicker.SelectedIndex;
            sortOption = index >= 0 && index < SortOptions.Length
                ? SortOptions[index]
                : SortOptions[0];
            ScheduleFiltering();
        };
        valueFilterButton.Clicked += async (_, _) => await ShowColumnFilterAsync();

        var actionButtons = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 8,
        };
        actionButtons.Add(downloadButton, 0);
        actionButtons.Add(shareButton, 1);

        var groupRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
        };
        groupRow.Add(groupPicker, 0, 0);
        groupRow.Add(new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = AppStrings.Get("GroupEpisodes"), VerticalOptions = LayoutOptions.Center },
                episodeGroupingSwitch,
            },
        }, 1, 0);

        var groupingAndSortRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 8,
        };
        groupingAndSortRow.Add(groupColumnPicker, 0);
        groupingAndSortRow.Add(sortPicker, 1);

        var columnFilterRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
        };
        columnFilterRow.Add(filterColumnPicker, 0);
        columnFilterRow.Add(valueFilterButton, 1);

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 8,
        };
        layout.Add(new Label
        {
            Text = AppStrings.Get("AppTitle"),
            FontSize = 23,
            FontAttributes = FontAttributes.Bold,
        }, 0, 0);
        layout.Add(urlEntry, 0, 1);
        layout.Add(actionButtons, 0, 2);
        layout.Add(searchEntry, 0, 3);
        layout.Add(groupRow, 0, 4);
        layout.Add(groupingAndSortRow, 0, 5);
        layout.Add(columnFilterRow, 0, 6);
        layout.Add(statusLabel, 0, 7);
        layout.Add(playlistView, 0, 8);
        Content = layout;
    }

    protected override void OnDisappearing()
    {
        filterCancellation?.Cancel();
        base.OnDisappearing();
    }

    private View CreatePlaylistRow()
    {
        var titleLabel = new Label
        {
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        titleLabel.SetBinding(Label.TextProperty, nameof(PlaylistRow.DisplayTitle));

        var detailLabel = new Label
        {
            FontSize = 12,
            TextColor = Colors.DimGray,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        detailLabel.SetBinding(Label.TextProperty, nameof(PlaylistRow.Detail));

        var copyButton = new Button
        {
            Text = AppStrings.Get("CopyUrl"),
            FontSize = 12,
            Padding = new Thickness(8, 4),
            VerticalOptions = LayoutOptions.Center,
        };
        copyButton.Clicked += async (sender, _) =>
        {
            if ((sender as Button)?.BindingContext is PlaylistRow { Entry: { } entry })
                await CopyStreamUrlAsync(entry);
        };
        copyButton.SetBinding(IsVisibleProperty, nameof(PlaylistRow.CanCopyUrl));

        var rowLayout = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            Padding = new Thickness(10, 7),
        };
        rowLayout.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children = { titleLabel, detailLabel },
        }, 0);
        rowLayout.Add(copyButton, 1);

        var border = new Border
        {
            Stroke = Color.FromArgb("#D8DEE8"),
            StrokeThickness = 1,
            StrokeShape = new MauiRoundRectangle { CornerRadius = 8 },
            Content = rowLayout,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (border.BindingContext is not PlaylistRow row)
                return;

            if (row.IsEpisodeGroup)
            {
                if (!expandedSeries.Add(row.SeriesKey!))
                    expandedSeries.Remove(row.SeriesKey!);
                ScheduleFiltering();
            }
            else if (row.Entry is { } entry)
            {
                await PlayStreamAsync(entry);
            }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private async Task DownloadPlaylistAsync()
    {
        var address = urlEntry.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await DisplayAlertAsync(
                AppStrings.Get("InvalidUrlTitle"),
                AppStrings.Get("InvalidUrlMessage"),
                AppStrings.Get("Ok"));
            return;
        }

#if !DEBUG
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            await DisplayAlertAsync(
                AppStrings.Get("HttpsRequiredTitle"),
                AppStrings.Get("HttpsRequiredMessage"),
                AppStrings.Get("Ok"));
            return;
        }
#endif

        downloadButton.IsEnabled = false;
        filterCancellation?.Cancel();
        statusLabel.Text = AppStrings.Get("Downloading");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var parsedEntries = await Task.Run(() => M3uParser.Parse(content));

            playlistContent = content;
            playlistAddress = address;
            entries.Clear();
            entries.AddRange(parsedEntries);
            expandedSeries.Clear();
            columnFilters.Clear();
            UpdateFilterButtonText();
            sortOption = SortOptions[0];
            sortPicker.SelectedIndex = 0;
            groupColumnName = null;
            groupColumnPicker.SelectedIndex = 0;
            shareButton.IsEnabled = true;
            PopulateGroups();
            Preferences.Set(LastPlaylistUrlKey, address);
            statusLabel.Text = AppStrings.Format("LoadedStatus", entries.Count, GetGroupCount());
            await ApplyFiltersAsync();
        }
        catch (HttpRequestException ex)
        {
            statusLabel.Text = AppStrings.Get("DownloadFailedStatus");
            await DisplayAlertAsync(
                AppStrings.Get("DownloadFailedTitle"),
                AppStrings.Format("DownloadFailedMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
        catch (WebException ex)
        {
            statusLabel.Text = AppStrings.Get("DownloadFailedStatus");
            await DisplayAlertAsync(
                AppStrings.Get("DownloadFailedTitle"),
                AppStrings.Format("DownloadFailedMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
        catch (TaskCanceledException ex)
        {
            statusLabel.Text = AppStrings.Get("DownloadTimeoutStatus");
            await DisplayAlertAsync(
                AppStrings.Get("DownloadFailedTitle"),
                AppStrings.Format("DownloadTimeoutMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
        catch (IOException ex)
        {
            statusLabel.Text = AppStrings.Get("ProcessingFailedStatus");
            await DisplayAlertAsync(
                AppStrings.Get("ReadErrorTitle"),
                AppStrings.Format("ReadErrorMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
        catch (ArgumentException ex)
        {
            statusLabel.Text = AppStrings.Get("ProcessingFailedStatus");
            await DisplayAlertAsync(
                AppStrings.Get("InvalidPlaylistTitle"),
                AppStrings.Format("InvalidPlaylistMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
        finally
        {
            downloadButton.IsEnabled = true;
            stopwatch.Stop();
            if (playlistContent is not null)
                statusLabel.Text += AppStrings.Format("DownloadDuration", stopwatch.Elapsed.TotalSeconds);
        }
    }

    private void PopulateGroups()
    {
        updatingGroups = true;
        groupPicker.Items.Clear();
        groupPicker.Items.Add(AllGroups);
        foreach (var group in entries
                     .Select(entry => entry.Group)
                     .Where(group => !string.IsNullOrWhiteSpace(group))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group, StringComparer.OrdinalIgnoreCase))
        {
            groupPicker.Items.Add(group);
        }

        groupPicker.SelectedIndex = 0;
        updatingGroups = false;
    }

    private int GetGroupCount() =>
        entries.Select(entry => entry.Group)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static string GetColumnLabel(string columnName) => columnName switch
    {
        "Name" => AppStrings.Get("ColumnName"),
        "Title" => AppStrings.Get("ColumnTitle"),
        "Group" => AppStrings.Get("ColumnGroup"),
        "Id" => AppStrings.Get("ColumnId"),
        "Url" => AppStrings.Get("ColumnUrl"),
        _ => columnName,
    };

    private static string GetColumnValue(M3uEntry entry, string columnName) => columnName switch
    {
        "Name" => entry.Name,
        "Title" => entry.Title,
        "Group" => entry.Group,
        "Id" => entry.Id,
        "Url" => entry.Url,
        _ => string.Empty,
    };

    private void UpdateFilterButtonText() =>
        valueFilterButton.Text = columnFilters.Count == 0
            ? AppStrings.Get("ChooseValues")
            : AppStrings.Format("ChooseValuesActive", columnFilters.Count);

    private async Task ShowColumnFilterAsync()
    {
        if (filterColumnPicker.SelectedIndex <= 0)
        {
            await DisplayAlertAsync(
                AppStrings.Get("ChooseColumnTitle"),
                AppStrings.Get("ChooseColumnMessage"),
                AppStrings.Get("Ok"));
            return;
        }

        var columnName = ColumnNames[filterColumnPicker.SelectedIndex - 1];
        var values = entries
            .Select(entry => GetColumnValue(entry, columnName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0)
        {
            await DisplayAlertAsync(
                AppStrings.Get("NoFilterValuesTitle"),
                AppStrings.Get("NoFilterValuesMessage"),
                AppStrings.Get("Ok"));
            return;
        }

        var selectedValues = columnFilters.TryGetValue(columnName, out var selected)
            ? new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        var filterPage = new ColumnValueFilterPage(GetColumnLabel(columnName), values, selectedValues);
        await Navigation.PushModalAsync(filterPage);
        var result = await filterPage.Result;

        if (result is null)
            return;

        if (result.Count == values.Length)
            columnFilters.Remove(columnName);
        else
            columnFilters[columnName] = result;

        UpdateFilterButtonText();
        ScheduleFiltering();
    }

    private void ScheduleFiltering()
    {
        filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        filterCancellation = cancellation;
        _ = FilterAfterDelayAsync(cancellation);
    }

    private async Task FilterAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            await ApplyFiltersAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(filterCancellation, cancellation))
                filterCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ApplyFiltersAsync(CancellationToken cancellationToken = default)
    {
        var source = entries.ToArray();
        var query = searchEntry.Text?.Trim() ?? string.Empty;
        var selectedGroup = groupPicker.SelectedItem as string ?? AllGroups;
        var groupEpisodes = episodeGroupingSwitch.IsToggled;
        var expanded = new HashSet<string>(expandedSeries, StringComparer.OrdinalIgnoreCase);
        var filters = columnFilters.ToDictionary(
            filter => filter.Key,
            filter => new HashSet<string>(filter.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
        var activeSort = sortOption;
        var activeGroupColumn = groupColumnName;
        statusLabel.Text = AppStrings.Get("Filtering");

        var result = await Task.Run(
            () => BuildVisibleRows(source, query, selectedGroup, groupEpisodes, expanded, filters, activeSort, activeGroupColumn, cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        visibleRows.Clear();
        foreach (var row in result.Rows)
            visibleRows.Add(row);

        statusLabel.Text = AppStrings.Format("VisibleEntries", result.EntryCount, entries.Count);
    }

    private static (List<PlaylistRow> Rows, int EntryCount) BuildVisibleRows(
        M3uEntry[] source,
        string query,
        string selectedGroup,
        bool groupEpisodes,
        HashSet<string> expanded,
        IReadOnlyDictionary<string, HashSet<string>> filters,
        SortOption sort,
        string? groupColumn,
        CancellationToken cancellationToken)
    {
        var matching = new List<M3uEntry>();
        for (var index = 0; index < source.Length; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var entry = source[index];
            if (selectedGroup != AllGroups &&
                !string.Equals(entry.Group, selectedGroup, StringComparison.OrdinalIgnoreCase))
                continue;
            if (query.Length > 0 && !entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filters.Any(filter => !filter.Value.Contains(GetColumnValue(entry, filter.Key))))
                continue;
            matching.Add(entry);
        }

        var rows = new List<PlaylistRow>(matching.Count);
        if (groupColumn is not null)
        {
            var groups = matching.GroupBy(
                entry => GetColumnValue(entry, groupColumn),
                StringComparer.OrdinalIgnoreCase);
            groups = sort.ColumnName == groupColumn
                ? (sort.Ascending
                    ? groups.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    : groups.OrderByDescending(group => group.Key, StringComparer.OrdinalIgnoreCase))
                : groups.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupEntries = SortEntries(group, sort);
                rows.Add(PlaylistRow.ForColumnGroup(
                    string.IsNullOrEmpty(group.Key) ? AppStrings.Get("EmptyValue") : group.Key,
                    groupEntries.Count));
                rows.AddRange(groupEntries.Select(entry => PlaylistRow.ForEntry(entry, entry.Name)));
            }

            return (rows, matching.Count);
        }

        if (!groupEpisodes)
        {
            rows.AddRange(SortEntries(matching, sort).Select(entry => PlaylistRow.ForEntry(entry, entry.Name)));
            return (rows, matching.Count);
        }

        var seriesGroups = new Dictionary<string, List<(M3uEntry Entry, int Season, int Episode)>>(
            StringComparer.OrdinalIgnoreCase);
        var ungrouped = new List<M3uEntry>();
        foreach (var entry in SortEntries(matching, sort))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = EpisodeTitleRegex.Match(entry.Name.Trim());
            if (!match.Success)
            {
                ungrouped.Add(entry);
                continue;
            }

            var series = match.Groups["series"].Value.Trim();
            if (series.Length == 0)
            {
                ungrouped.Add(entry);
                continue;
            }

            if (!seriesGroups.TryGetValue(series, out var episodes))
            {
                episodes = [];
                seriesGroups.Add(series, episodes);
            }

            episodes.Add((
                entry,
                int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture)));
        }

        foreach (var series in seriesGroups.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodes = seriesGroups[series]
                .OrderBy(item => item.Season)
                .ThenBy(item => item.Episode)
                .ToArray();
            var isExpanded = expanded.Contains(series);
            rows.Add(PlaylistRow.ForSeries(series, episodes.Length, isExpanded));
            if (isExpanded)
            {
                foreach (var episode in episodes)
                    rows.Add(PlaylistRow.ForEntry(episode.Entry, $"S{episode.Season:00} E{episode.Episode:00}"));
            }
        }

        rows.AddRange(ungrouped.Select(entry => PlaylistRow.ForEntry(entry, entry.Name)));
        return (rows, matching.Count);
    }

    private static List<M3uEntry> SortEntries(IEnumerable<M3uEntry> entries, SortOption sort)
    {
        if (sort.ColumnName is null)
            return entries.ToList();

        return (sort.Ascending
            ? entries.OrderBy(entry => GetColumnValue(entry, sort.ColumnName), StringComparer.OrdinalIgnoreCase)
            : entries.OrderByDescending(entry => GetColumnValue(entry, sort.ColumnName), StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task PlayStreamAsync(M3uEntry entry)
    {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
        {
            await DisplayAlertAsync(
                AppStrings.Get("InvalidStreamUrlTitle"),
                AppStrings.Get("InvalidStreamUrlMessage"),
                AppStrings.Get("Ok"));
            return;
        }

#if !DEBUG
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            await DisplayAlertAsync(
                AppStrings.Get("HttpsRequiredTitle"),
                AppStrings.Get("HttpsRequiredMessage"),
                AppStrings.Get("Ok"));
            return;
        }
#endif

        try
        {
            await Launcher.Default.OpenAsync(uri);
            statusLabel.Text = AppStrings.Format("StreamOpened", entry.Name);
        }
        catch (FeatureNotSupportedException ex)
        {
            await DisplayAlertAsync(
                AppStrings.Get("PlaybackUnavailableTitle"),
                AppStrings.Format("PlaybackUnavailableMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
        catch (InvalidOperationException ex)
        {
            await DisplayAlertAsync(
                AppStrings.Get("PlaybackFailedTitle"),
                ex.Message,
                AppStrings.Get("Ok"));
        }
    }

    private async Task CopyStreamUrlAsync(M3uEntry entry)
    {
        await Clipboard.Default.SetTextAsync(entry.Url);
        statusLabel.Text = AppStrings.Get("UrlCopied");
    }

    private async Task SharePlaylistAsync()
    {
        if (playlistContent is null)
            return;

        try
        {
            var extension = Uri.TryCreate(playlistAddress, UriKind.Absolute, out var uri) &&
                            uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? ".m3u8"
                : ".m3u";
            var path = Path.Combine(FileSystem.CacheDirectory, $"playlist{extension}");
            await File.WriteAllTextAsync(path, playlistContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await Share.Default.RequestAsync(new ShareFileRequest(
                AppStrings.Get("ShareTitle"),
                new ShareFile(path)));
        }
        catch (FeatureNotSupportedException ex)
        {
            await DisplayAlertAsync(
                AppStrings.Get("ShareUnavailableTitle"),
                ex.Message,
                AppStrings.Get("Ok"));
        }
        catch (IOException ex)
        {
            await DisplayAlertAsync(
                AppStrings.Get("StorageErrorTitle"),
                AppStrings.Format("StorageErrorMessage", ex.Message),
                AppStrings.Get("Ok"));
        }
    }

    private sealed class PlaylistRow
    {
        private PlaylistRow(M3uEntry? entry, string displayTitle, string detail, string? seriesKey = null)
        {
            Entry = entry;
            DisplayTitle = displayTitle;
            Detail = detail;
            SeriesKey = seriesKey;
        }

        public M3uEntry? Entry { get; }
        public string DisplayTitle { get; }
        public string Detail { get; }
        public string? SeriesKey { get; }
        public bool IsEpisodeGroup => SeriesKey is not null;
        public bool CanCopyUrl => Entry is not null;

        public static PlaylistRow ForEntry(M3uEntry entry, string displayTitle) =>
            new(entry, displayTitle, string.Join(" · ", new[] { entry.Title, entry.Group, entry.Id }.Where(value => !string.IsNullOrWhiteSpace(value))));

        public static PlaylistRow ForSeries(string name, int count, bool expanded) =>
            new(
                null,
                $"{(expanded ? "▼" : "▶")} {name} ({count:N0})",
                AppStrings.Get("SeriesCollapsedDetail"),
                name);

        public static PlaylistRow ForColumnGroup(string name, int count) =>
            new(null, $"{name} ({count:N0})", AppStrings.Get("GroupedEntriesDetail"));
    }

    private readonly record struct SortOption(string Label, string? ColumnName, bool Ascending);

    private sealed class ColumnValueFilterPage : ContentPage
    {
        private readonly ObservableCollection<FilterValueOption> visibleValues = [];
        private readonly FilterValueOption[] allValues;
        private readonly TaskCompletionSource<HashSet<string>?> result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SearchBar searchBar = new() { Placeholder = AppStrings.Get("FilterSearchPlaceholder") };
        private readonly CollectionView valueList = new()
        {
            SelectionMode = SelectionMode.None,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
        };

        public Task<HashSet<string>?> Result => result.Task;

        public ColumnValueFilterPage(string columnName, IReadOnlyList<string> values, HashSet<string> selectedValues)
        {
            Title = AppStrings.Format("FilterTitle", columnName);
            allValues = values.Select(value => new FilterValueOption(
                value,
                selectedValues.Contains(value))).ToArray();
            valueList.ItemsSource = visibleValues;
            valueList.ItemTemplate = new DataTemplate(CreateValueRow);
            searchBar.TextChanged += (_, _) => RefreshValues();

            var selectAllButton = new Button { Text = AppStrings.Get("SelectAll") };
            selectAllButton.Clicked += (_, _) => SetVisibleValuesSelected(true);
            var selectNoneButton = new Button { Text = AppStrings.Get("SelectNone") };
            selectNoneButton.Clicked += (_, _) => SetVisibleValuesSelected(false);
            var applyButton = new Button { Text = AppStrings.Get("Apply") };
            applyButton.Clicked += async (_, _) =>
            {
                var selected = new HashSet<string>(
                    allValues.Where(value => value.IsSelected).Select(value => value.Value),
                    StringComparer.OrdinalIgnoreCase);
                await CloseAsync(selected);
            };
            var cancelButton = new Button { Text = AppStrings.Get("Cancel") };
            cancelButton.Clicked += async (_, _) => await CloseAsync(null);

            var selectionButtons = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Star),
                },
                ColumnSpacing = 8,
            };
            selectionButtons.Add(selectAllButton, 0);
            selectionButtons.Add(selectNoneButton, 1);
            var actionButtons = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Star),
                },
                ColumnSpacing = 8,
            };
            actionButtons.Add(cancelButton, 0);
            actionButtons.Add(applyButton, 1);

            var layout = new Grid
            {
                Padding = new Thickness(14, 8),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                },
                RowSpacing = 8,
            };
            layout.Add(new Label
            {
                Text = Title,
                FontSize = 20,
                FontAttributes = FontAttributes.Bold,
            }, 0, 0);
            layout.Add(searchBar, 0, 1);
            layout.Add(valueList, 0, 2);
            layout.Add(selectionButtons, 0, 3);
            layout.Add(actionButtons, 0, 4);
            Content = layout;
            RefreshValues();
        }

        protected override void OnDisappearing()
        {
            result.TrySetResult(null);
            base.OnDisappearing();
        }

        private async Task CloseAsync(HashSet<string>? selectedValues)
        {
            result.TrySetResult(selectedValues);
            await Navigation.PopModalAsync();
        }

        private View CreateValueRow()
        {
            var checkBox = new CheckBox();
            checkBox.SetBinding(CheckBox.IsCheckedProperty, nameof(FilterValueOption.IsSelected), BindingMode.TwoWay);
            var label = new Label
            {
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            label.SetBinding(Label.TextProperty, nameof(FilterValueOption.DisplayValue));
            return new HorizontalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(6, 2),
                Children = { checkBox, label },
            };
        }

        private void RefreshValues()
        {
            var query = searchBar.Text?.Trim() ?? string.Empty;
            visibleValues.Clear();
            foreach (var value in allValues.Where(value =>
                         query.Length == 0 || value.DisplayValue.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                visibleValues.Add(value);
            }
        }

        private void SetVisibleValuesSelected(bool selected)
        {
            foreach (var value in visibleValues)
                value.IsSelected = selected;
        }
    }

    private sealed class FilterValueOption : INotifyPropertyChanged
    {
        private bool isSelected;

        public FilterValueOption(string value, bool isSelected)
        {
            Value = value;
            this.isSelected = isSelected;
        }

        public string Value { get; }
        public string DisplayValue => string.IsNullOrEmpty(Value) ? AppStrings.Get("EmptyValue") : Value;

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                    return;

                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
