using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Telnet;

namespace DuCom.ViewModels;

public partial class ToolCenterViewModel
{
    [ObservableProperty]
    public partial int TelnetPort { get; set; } = 23;

    [ObservableProperty]
    public partial bool TelnetAllowRemote { get; set; }

    [ObservableProperty]
    public partial bool TelnetAuthenticationEnabled { get; set; }

    [ObservableProperty]
    public partial string TelnetUsername { get; set; } = string.Empty;

    public string TelnetPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TelnetListenAddressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTelnetRunning { get; private set; }

    [ObservableProperty]
    public partial int TelnetClientCount { get; private set; }

    [ObservableProperty]
    public partial bool IsBridgeBound { get; private set; }

    [ObservableProperty]
    public partial string TelnetBridgeStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BridgePortName { get; set; } = string.Empty;

    public ObservableCollection<string> BridgePortOptions { get; } = [];

    public ObservableCollection<string> TelnetClients { get; } = [];

    [RelayCommand]
    private async Task ToggleTelnetAsync()
    {
        if (_telnetBridge.IsRunning)
        {
            await _telnetBridge.StopAsync();
        }
        else
        {
            if (TelnetAllowRemote && !TelnetAuthenticationEnabled)
            {
                TelnetBridgeStatus = GetResourceString("Tools.TelnetRemoteAuthenticationRequired");
                return;
            }

            try
            {
                _telnetBridge.ConfigureAuthentication(new TelnetAuthenticationOptions(
                    TelnetAuthenticationEnabled,
                    TelnetUsername.Trim(),
                    TelnetPassword));
            }
            catch (ArgumentException)
            {
                TelnetBridgeStatus = GetResourceString("Tools.TelnetCredentialsRequired");
                return;
            }

            // Loopback by default; IPAddress.Any only through the explicit remote opt-in.
            _telnetBridge.Start(new TelnetListenOptions(TelnetPort, TelnetAllowRemote, TelnetAuthenticationEnabled));
        }

        UpdateTelnetStatus();
    }

    internal async Task SmokeTelnetAsync()
    {
        int originalPort = TelnetPort;
        bool originalAllowRemote = TelnetAllowRemote;
        bool originalAuthentication = TelnetAuthenticationEnabled;
        try
        {
            TelnetPort = 23_230;
            TelnetAllowRemote = false;
            TelnetAuthenticationEnabled = false;
            await ToggleTelnetAsync();
            if (!IsTelnetRunning)
            {
                throw new InvalidOperationException("Telnet service failed to start.");
            }

            await ToggleTelnetAsync();
            if (IsTelnetRunning)
            {
                throw new InvalidOperationException("Telnet service failed to stop.");
            }
        }
        finally
        {
            TelnetPort = originalPort;
            TelnetAllowRemote = originalAllowRemote;
            TelnetAuthenticationEnabled = originalAuthentication;
        }
    }

    [RelayCommand]
    private void RefreshBridgePorts()
    {
        BridgePortOptions.Clear();
        if (_mainViewModel is null)
        {
            return;
        }

        foreach (SessionViewModel session in _mainViewModel.Sessions.Where(session => session.IsOpen))
        {
            BridgePortOptions.Add(session.PortName);
        }

        if (BridgePortOptions.Count > 0 && !BridgePortOptions.Contains(BridgePortName))
        {
            BridgePortName = BridgePortOptions[0];
        }
    }

    [RelayCommand]
    private void ToggleBridge()
    {
        if (_telnetBridge.IsBound)
        {
            _telnetBridge.Unbind();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(BridgePortName))
            {
                TelnetBridgeStatus = GetResourceString("Tools.BridgeNoPort");
                return;
            }

            if (!IsTelnetRunning)
            {
                if (TelnetAllowRemote && !TelnetAuthenticationEnabled)
                {
                    TelnetBridgeStatus = GetResourceString("Tools.TelnetRemoteAuthenticationRequired");
                    return;
                }

                try
                {
                    _telnetBridge.ConfigureAuthentication(new TelnetAuthenticationOptions(
                        TelnetAuthenticationEnabled,
                        TelnetUsername.Trim(),
                        TelnetPassword));
                }
                catch (ArgumentException)
                {
                    TelnetBridgeStatus = GetResourceString("Tools.TelnetCredentialsRequired");
                    return;
                }

                _telnetBridge.Start(new TelnetListenOptions(TelnetPort, TelnetAllowRemote, TelnetAuthenticationEnabled));
                UpdateTelnetStatus();
            }

            _telnetBridge.Bind(BridgePortName);
        }

        IsBridgeBound = _telnetBridge.IsBound;
        TelnetBridgeStatus = _telnetBridge.IsBound
            ? GetResourceString("Tools.BridgeBound").Replace("{0}", _telnetBridge.BoundPortName ?? string.Empty, StringComparison.Ordinal)
            : string.Empty;
    }

    private void OnTelnetStatusChanged(object? sender, EventArgs e) =>
        App.Current.Dispatcher.BeginInvoke(UpdateTelnetStatus);

    private void UpdateTelnetStatus()
    {
        IsTelnetRunning = _telnetBridge.IsRunning;
        TelnetClientCount = _telnetBridge.ClientCount;
        // Always show what the listener is actually bound to, so an accidentally remote
        // listener is visible in the UI.
        TelnetListenAddressText = _telnetBridge.IsRunning
            ? _telnetBridge.LocalEndPoint ?? string.Empty
            : (TelnetAllowRemote ? "0.0.0.0" : "127.0.0.1");
        TelnetClients.Clear();
        foreach (string endpoint in _telnetBridge.ClientEndpoints)
        {
            TelnetClients.Add(endpoint);
        }
    }

    partial void OnTelnetPortChanged(int value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetPort = Math.Clamp(value, 1, 65_535);
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }
    }

    partial void OnTelnetAllowRemoteChanged(bool value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetAllowRemote = value;
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }

        UpdateTelnetStatus();
    }

    partial void OnTelnetAuthenticationEnabledChanged(bool value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetAuthenticationEnabled = value;
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }
    }

    partial void OnTelnetUsernameChanged(string value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetUsername = value;
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }
    }

    private void OnTelnetDiagnostic(string message)
    {
        Program.DiagnosticLog?.Warning(message);
        App.Current.Dispatcher.BeginInvoke(() => TelnetBridgeStatus = message);
    }
}
