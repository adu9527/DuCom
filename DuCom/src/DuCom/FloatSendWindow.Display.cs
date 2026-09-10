using DuCom.Core.Sending;
using DuCom.Core.Sessions;

namespace DuCom;

public partial class FloatSendWindow
{
    private SessionTapDisplayFormat SelectFormat()
    {
        SendMode? replyMode = _session.DisplayTaps.ResolveReplyWindowFormat(_replyWindowMs);
        if (replyMode.HasValue)
        {
            // Reply-window rule: within the window, replies render in the sent mode.
            return replyMode.Value == SendMode.Hex ? SessionTapDisplayFormat.Hex : SessionTapDisplayFormat.Str;
        }

        return _recvShowHex ? SessionTapDisplayFormat.Hex : SessionTapDisplayFormat.Str;
    }

    private void EnqueueText(string text)
    {
        if (string.IsNullOrEmpty(text) || _isClosed)
        {
            return;
        }

        lock (_pendingGate)
        {
            _pendingText.Enqueue(text);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        Dispatcher.BeginInvoke(FlushPendingText, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushPendingText()
    {
        string text;
        lock (_pendingGate)
        {
            if (_pendingText.Count == 0)
            {
                _flushScheduled = false;
                return;
            }

            text = string.Concat(_pendingText);
            _pendingText.Clear();
        }

        try
        {
            AppendStreamText(text);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Float send window flush failed. Port={PortName}.", exception);
        }

        lock (_pendingGate)
        {
            if (_pendingText.Count == 0)
            {
                _flushScheduled = false;
                return;
            }
        }

        Dispatcher.BeginInvoke(FlushPendingText, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AppendStreamText(string text)
    {
        _lineBuffer.Append(text);
        string buffer = _lineBuffer.ToString();
        _lineBuffer.Clear();

        bool added = false;
        int start = 0;
        int newlineIndex;
        while ((newlineIndex = buffer.IndexOf('\n', start)) >= 0)
        {
            string piece = buffer[start..newlineIndex].TrimEnd('\r');
            FinalizeTail(piece);
            added = true;
            start = newlineIndex + 1;
        }

        string remainder = buffer[start..];
        if (remainder.Length > 0)
        {
            ExtendTail(remainder);
            added = true;
        }

        if (!added)
        {
            return;
        }

        TrimBuffer();
        if (_fixedLog && _lastSendAnchor is not null)
        {
            // Fixed-scroll reply positioning: jump back to the line captured at send time.
            TapLine anchor = _lastSendAnchor;
            _lastSendAnchor = null;
            LogList.ScrollIntoView(anchor);
        }
    }

    private void FinalizeTail(string piece)
    {
        if (_tailLine is null)
        {
            Lines.Add(new TapLine(piece));
        }
        else
        {
            _tailLine = new TapLine(_tailLine.Text + piece);
            Lines[^1] = _tailLine;
            _tailLine = null;
            return;
        }

        _bufferCharacters += piece.Length + 2;
    }

    private void ExtendTail(string piece)
    {
        if (_tailLine is null)
        {
            _tailLine = new TapLine(piece);
            Lines.Add(_tailLine);
            _bufferCharacters += piece.Length;
        }
        else
        {
            _tailLine = new TapLine(_tailLine.Text + piece);
            Lines[^1] = _tailLine;
            _bufferCharacters += piece.Length;
        }
    }

    private void TrimBuffer()
    {
        while (Lines.Count > MaximumLineCount ||
               _bufferCharacters > MaximumBufferCharacters && Lines.Count > 1)
        {
            TapLine first = Lines[0];
            Lines.RemoveAt(0);
            _bufferCharacters -= first.Text.Length + 2;
            if (ReferenceEquals(first, _tailLine))
            {
                _tailLine = null;
            }

            if (ReferenceEquals(first, _lastSendAnchor))
            {
                _lastSendAnchor = null;
            }
        }
    }
}
