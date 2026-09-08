using System.Text.Json;
using DuCom.Plugin;

namespace Example.HelloDuCom;

/// <summary>
/// Minimal third-party DPP/1 plugin. It subscribes to the receive side channel of the first
/// open serial session, counts received bytes, and renders a small tool page with a live
/// counter. Intentionally small: copy this project to start your own plugin.
/// </summary>
public sealed class HelloPlugin : DuComPlugin
{
    private readonly Lock _gate = new();
    private long _receivedBytes;
    private long _receivedBlocks;
    private string _lastSession = "-";

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string? stored = await Api.Storage.ReadAsync(cancellationToken);
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                if (JsonDocument.Parse(stored).RootElement.TryGetProperty("receivedBytes", out JsonElement element))
                {
                    _receivedBytes = element.GetInt64();
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    public override async Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions = await Api.Serial.ListSessionsAsync(cancellationToken);
        foreach (DuCom.Plugin.Dto.SerialSessionInfo session in sessions.Where(session => session.Open))
        {
            if (await Api.Serial.SubscribeAsync(session.SessionId, cancellationToken))
            {
                lock (_gate)
                {
                    _lastSession = session.Port;
                }

                break;
            }
        }

        Api.Serial.DataReceived += (_, args) =>
        {
            lock (_gate)
            {
                _receivedBytes += args.Data.Length;
                _receivedBlocks++;
            }
        };

        await PushPageAsync();
        return new PluginActivation
        {
            Menus =
            [
                new MenuContribution
                {
                    ContributionId = "open-hello",
                    Label = "Hello DuCom",
                    CommandId = "open",
                    PageId = "hello",
                },
            ],
            ToolPages =
            [
                new ToolPageContribution
                {
                    ContributionId = "hello",
                    Title = "Hello DuCom",
                    Nodes =
                    [
                        new UiLabelNode { Text = "Hello from a sandboxed plugin process!", Style = UiTextStyle.Heading },
                        new UiLabelNode { Text = "This page is rendered by the DuCom host from a declarative description." },
                        new UiButtonNode { CommandId = "reset-counter", Text = "Reset counter" },
                    ],
                },
            ],
        };
    }

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        if (commandId == "reset-counter")
        {
            lock (_gate)
            {
                _receivedBytes = 0;
                _receivedBlocks = 0;
            }

            await PushPageAsync();
            return CommandInvokeOutcome.Complete();
        }

        return CommandInvokeOutcome.Reject($"Unknown command '{commandId}'.");
    }

    public override Task DeactivateAsync(CancellationToken cancellationToken) =>
        Api.Serial.UnsubscribeAllAsync(cancellationToken);

    private async Task PushPageAsync()
    {
        long bytes, blocks;
        string session;
        lock (_gate)
        {
            bytes = _receivedBytes;
            blocks = _receivedBlocks;
            session = _lastSession;
        }

        await Api.Ui.UpdateToolPageAsync("hello",
        [
            new UiLabelNode { Text = "Hello from a sandboxed plugin process!", Style = UiTextStyle.Heading },
            new UiLabelNode { Text = $"Session: {session}" },
            new UiLabelNode { Text = $"Received blocks: {blocks:N0}" },
            new UiLabelNode { Text = $"Received bytes: {bytes:N0}" },
            new UiButtonNode { CommandId = "reset-counter", Text = "Reset counter" },
        ]);
    }
}
