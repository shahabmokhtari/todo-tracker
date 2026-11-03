using System.Text;
using OneThing.Cli;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var cancel = new CancellationTokenSource();
if (CliApp.StopsOnCtrlC(args))
{
    // The MCP server shuts down cleanly on Ctrl+C; every other command just stops.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
}
return await CliApp.RunAsync(args, CliContext.ForConsole(), cancel.Token).ConfigureAwait(false);
