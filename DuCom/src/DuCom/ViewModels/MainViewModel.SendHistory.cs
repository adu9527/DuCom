using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private readonly SendHistory _sendHistory = new();
    private readonly SendHistoryNavigator _sendHistoryNavigator;

    /// <summary>
    /// Interactive up/down history navigation for the send editor. Returns the applied text or
    /// null when nothing changed (history empty, already at boundary).
    /// </summary>
    public string? NavigateSendHistory(bool previous, string currentText)
    {
        string? applied = previous ? _sendHistoryNavigator.MovePrevious(currentText) : _sendHistoryNavigator.MoveNext();
        if (applied is not null && SelectedSession is not null)
        {
            SelectedSession.SendText = applied;
        }

        return applied;
    }

    internal IReadOnlyList<string> SendHistoryEntries => _sendHistory.Entries;

    internal IReadOnlyList<string> SearchSendHistoryEntries(string? query) => _sendHistory.Search(query);

    internal void UseSendHistoryEntry(string entry)
    {
        if (SelectedSession is not null)
        {
            SelectedSession.SendText = entry;
        }
    }

    internal void DeleteSendHistoryEntry(string entry)
    {
        List<string> remaining = [.. _sendHistory.Entries.Where(item => item != entry)];
        _sendHistory.Replace(remaining);
        PersistSendHistory();
    }

    internal void ClearSendHistory()
    {
        _sendHistory.Clear();
        PersistSendHistory();
    }

    private void PersistSendHistory()
    {
        try
        {
            SendHistoryFileService.Save(_sendHistory);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to save send history.", exception);
        }
    }
}
