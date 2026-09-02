using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Search;
using DuCom.Core.Storage;

namespace DuCom.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private const int DebounceMilliseconds = 200;
    private readonly Dispatcher _dispatcher;
    private Func<LineStoreSnapshot?> _snapshotProvider = static () => null;
    private CancellationTokenSource? _debounceSource;
    private CancellationTokenSource? _searchSource;
    private int _searchGeneration;
    private SearchMatch[] _matches = [];
    private CompositeFormat? _resultsFormat;

    public SearchViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool MatchWholeLine { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMatchDisplay))]
    [NotifyPropertyChangedFor(nameof(CurrentMatch))]
    public partial int CurrentMatchIndex { get; private set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    public partial string StatusText { get; private set; } = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    public int TotalMatches => _matches.Length;

    public SearchMatch? CurrentMatch => CurrentMatchIndex >= 0 && CurrentMatchIndex < _matches.Length
        ? _matches[CurrentMatchIndex]
        : null;

    public string CurrentMatchDisplay => TotalMatches > 0 && CurrentMatchIndex >= 0
        ? string.Format(CultureInfo.InvariantCulture, ResultsFormat, CurrentMatchIndex + 1, TotalMatches)
        : TotalMatches.ToString(CultureInfo.InvariantCulture);

    private CompositeFormat ResultsFormat => _resultsFormat ??= CompositeFormat.Parse(GetResourceString("Search.ResultsFormat"));

    public event EventHandler? FocusRequested;

    public void AttachSnapshotProvider(Func<LineStoreSnapshot?> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        // Session identity changed: stale in-flight snapshot/search work must never render
        // against the new session's context.
        InvalidatePendingWork();
        if (IsOpen)
        {
            _ = DebouncedSearchAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync();

    partial void OnUseRegexChanged(bool value) => _ = DebouncedSearchAsync();

    partial void OnMatchCaseChanged(bool value) => _ = DebouncedSearchAsync();

    partial void OnMatchWholeLineChanged(bool value) => _ = DebouncedSearchAsync();

    [RelayCommand]
    private void Open()
    {
        IsOpen = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
        _ = DebouncedSearchAsync();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        CancelPendingWork();
        ClearResults();
    }

    /// <summary>Cancels debounce/search work and invalidates any in-flight result delivery.</summary>
    private void CancelPendingWork()
    {
        Interlocked.Increment(ref _searchGeneration);
        _debounceSource?.Cancel();
        _searchSource?.Cancel();
        _matches = [];
        CurrentMatchIndex = -1;
        OnPropertyChanged(nameof(CurrentMatch));
        OnPropertyChanged(nameof(CurrentMatchDisplay));
        OnPropertyChanged(nameof(TotalMatches));
    }

    private void InvalidatePendingWork() => CancelPendingWork();

    [RelayCommand]
    private void ToggleRegex() => UseRegex = !UseRegex;

    [RelayCommand]
    private void ToggleMatchCase() => MatchCase = !MatchCase;

    [RelayCommand]
    private void ToggleMatchWholeLine() => MatchWholeLine = !MatchWholeLine;

    [RelayCommand]
    private void FindNext()
    {
        if (_matches.Length == 0)
        {
            return;
        }

        CurrentMatchIndex = CurrentMatchIndex < 0 || CurrentMatchIndex >= _matches.Length - 1
            ? 0
            : CurrentMatchIndex + 1;
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (_matches.Length == 0)
        {
            return;
        }

        CurrentMatchIndex = CurrentMatchIndex <= 0
            ? _matches.Length - 1
            : CurrentMatchIndex - 1;
    }

    private async Task DebouncedSearchAsync()
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(DebouncedSearchAsync);
            return;
        }

        _debounceSource?.Cancel();
        CancellationTokenSource cts = new();
        _debounceSource = cts;

        try
        {
            await Task.Delay(DebounceMilliseconds, cts.Token).ConfigureAwait(true);
            await SearchAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return;
        }

        SearchRequest request = new(SearchText, UseRegex, MatchCase, MatchWholeLine);
        if (snapshotOrPatternEmpty(request))
        {
            await ApplyResultAsync(SearchResult.Empty).ConfigureAwait(false);
            return;
        }

        int generation = Interlocked.Increment(ref _searchGeneration);
        _searchSource?.Cancel();
        CancellationTokenSource cts = new();
        _searchSource = cts;
        CancellationToken linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token).Token;

        SearchResult result;
        try
        {
            // The snapshot copy and the search both run on the thread pool; the UI thread never
            // performs a full line-store walk while the search bar is active.
            result = await Task.Run(
                () => SafeSearchExecutor.Execute(
                    () => _snapshotProvider(),
                    request,
                    OnSnapshotProviderError,
                    linkedToken),
                linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ApplyResultAsync(result, generation).ConfigureAwait(false);
    }

    private static bool snapshotOrPatternEmpty(SearchRequest request) =>
        string.IsNullOrEmpty(request.Pattern);

    private static void OnSnapshotProviderError(Exception exception) =>
        Program.DiagnosticLog?.Warning($"Search snapshot provider failed. {exception.Message}");

    private async Task ApplyResultAsync(SearchResult result, int? expectedGeneration = null)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _searchGeneration)
            {
                return;
            }

            if (result.IsCancelled || !IsOpen)
            {
                return;
            }

            _matches = result.Matches.ToArray();
            CurrentMatchIndex = _matches.Length > 0 ? 0 : -1;
            UpdateStatusText(result);
            OnPropertyChanged(nameof(TotalMatches));
            OnPropertyChanged(nameof(CurrentMatch));
        });
    }

    private void ClearResults()
    {
        _matches = [];
        CurrentMatchIndex = -1;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(TotalMatches));
        OnPropertyChanged(nameof(CurrentMatchDisplay));
        OnPropertyChanged(nameof(CurrentMatch));
    }

    private void UpdateStatusText(SearchResult result)
    {
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            StatusText = result.ErrorMessage == DuCom.Core.Search.LogSearchEngine.RegexTimeoutMessage
                ? GetResourceString("Search.RegexTimeout")
                : GetResourceString("Search.RegexError") + ": " + result.ErrorMessage;
        }
        else if (_matches.Length == 0)
        {
            StatusText = GetResourceString("Search.NoResults");
        }
        else
        {
            StatusText = string.Empty;
        }

        OnPropertyChanged(nameof(CurrentMatchDisplay));
        OnPropertyChanged(nameof(CurrentMatch));
    }

    private static string GetResourceString(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}
