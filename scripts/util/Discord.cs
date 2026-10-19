using System;
using DiscordRPC;
using Godot;

public class Discord
{
    private const string AppId = "1231699688340590722";

    /// <summary>
    /// Rich presence talks to Discord over a local IPC socket, which no mobile OS
    /// provides. Skip it on Android instead of letting a dead socket fail at startup.
    /// </summary>
    private static readonly bool Supported = OS.GetName() is "Windows" or "Linux" or "macOS";

    public static DiscordRpcClient Client { get; private set; }

    static Discord()
    {
        if (!Supported)
            return;

        try
        {
            Client = new DiscordRpcClient(AppId);
            Client.Initialize();
            Client.SetPresence(new RichPresence { Assets = new Assets { LargeImageKey = "short" } });
        }
        catch (Exception ex)
        {
            Logger.Log($"Rich presence unavailable: {ex.Message}", true);
            Client = null;
        }
    }

    public static void SetState(string state) => Client?.UpdateState(state);

    public static void Dispose()
    {
        Client?.Dispose();
        Client = null;
    }
}
