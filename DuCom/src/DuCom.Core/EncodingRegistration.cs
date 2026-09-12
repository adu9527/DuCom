using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace DuCom.Core;

internal static class EncodingRegistration
{
    // GB2312/GBK resolve only after the CodePages provider is registered; the built-in
    // .NET encoding set covers just UTF-8/16/32, ASCII and Latin-1. The module initializer
    // runs before any DuCom.Core method body, so every host process (app, PluginHost,
    // PluginWorker, tests) is covered without per-entry-point wiring. This is a deliberate
    // library use of the attribute, hence the CA2255 suppression.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:ModuleInitializer attribute should not be used in libraries",
        Justification = "GB2312/GBK must resolve in every host that loads DuCom.Core (app, PluginHost, PluginWorker, tests); per-entry-point registration would miss future hosts.")]
    internal static void RegisterCodePagesProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
