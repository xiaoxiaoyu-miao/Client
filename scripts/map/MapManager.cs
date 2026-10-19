using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class MapManager : Node
{
    public static Bindable<Map> Selected { get; set; } = new(null);

    public static List<Map> Maps { get; set; } = [];

    public static event Action<Map> MapDeleted;

    public static event Action<Map> MapUpdated;

    public static bool Initialized = false;

    public static event Action<List<Map>> MapsInitialized;

    public override async void _Ready()
    {
        MapCache.Initialize();

        // Import anything the player dropped in first, so the map list below sees it.
        await importDroppedMaps();

        await Task.Run(() =>
        {
            MapCache.Load(true);

            if (!Initialized)
            {
                Initialized = true;
                MapsInitialized?.Invoke(Maps);
            }
        });
    }

    /// <summary>
    /// Android has no usable file picker for arbitrary folders, so map files copied into
    /// user://import are imported at startup and then moved to user://import/imported.
    /// </summary>
    private static async Task importDroppedMaps()
    {
        try
        {
            string folder = Path.Combine(Constants.USER_FOLDER, "import");
            Directory.CreateDirectory(folder);

            string[] files = Directory
                .GetFiles(folder)
                .Where(path => MapParser.IsValidExt(Path.GetExtension(path).TrimStart('.').ToLowerInvariant()))
                .ToArray();

            if (files.Length == 0)
            {
                return;
            }

            Logger.Log($"Importing {files.Length} map file(s) from {folder}");
            await MapParser.BulkImport(files, true);

            string imported = Path.Combine(folder, "imported");
            Directory.CreateDirectory(imported);

            foreach (var file in files)
            {
                try
                {
                    File.Move(file, Path.Combine(imported, Path.GetFileName(file)), true);
                }
                catch (Exception exception)
                {
                    Logger.Error(exception);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
        }
    }

    public static void Select(Map map)
    {
        Selected.Value = GetMapById(map.Id);
    }

    public static Map GetMapById(int id)
    {
        //     return Maps.Where(x => x.Id == id).First();
        return Maps.FirstOrDefault(x => x.Id == id);
    }

    public static void Update(Map map)
    {
        MapCache.UpdateMap(map);
        MapUpdated?.Invoke(map);
    }

    public static void InsertVideo(Map map, string path)
    {
        Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        byte[] videoBuffer = file.GetBuffer((long)file.GetLength());
        file.Close();

        map.VideoBuffer = videoBuffer;

        var oldmap = MapParser.Decode(map.FolderPath);

        map.Mappers = map.CachedMappers.Split("_");
        map.AudioBuffer = oldmap.AudioBuffer;
        map.CoverBuffer = oldmap.CoverBuffer;

        Directory.Delete(map.FolderPath);

        MapParser.Encode(map);
        map.FolderPath = MapCache.GetMd5Checksum(map.FolderPath);
        Update(map);
    }

    public static void RemoveVideo(Map map)
    {
        _ = Godot.FileAccess.Open(map.FolderPath, Godot.FileAccess.ModeFlags.Read);

        map.VideoBuffer = null;

        var oldmap = MapParser.Decode(map.FolderPath);

        map.Mappers = map.PrettyMappers.Split(" ");
        map.AudioBuffer = oldmap.AudioBuffer;
        map.CoverBuffer = oldmap.CoverBuffer;

        File.Delete(map.FolderPath);

        MapParser.Encode(map);
        map.MetadataObjectHash = MapCache.GetMd5Checksum(map.FolderPath);
        Update(map);
    }

    public static void Delete(Map map)
    {
        try
        {
            try
            {
                Directory.Delete(map.FolderPath, true);
                if (!Directory.Exists(map.FolderPath))
                {
                    Logger.Log($"{map.Title} has been deleted");
                }
            }
            catch
            {
                if (File.Exists(map.FolderPath) || Directory.Exists(map.FolderPath))
                {
                    Logger.Error("Unable to delete map");
                    return;
                }
            }

            MapCache.RemoveMap(map);
            Maps.RemoveAll(x => x.Id == map.Id);

            Callable
                .From(() =>
                {
                    _ = ToastNotification.Notify($"Deleted {map.PrettyTitle}!");
                })
                .CallDeferred();
            Callable.From(() => MapDeleted?.Invoke(map)).CallDeferred();
        }
        catch (Exception e)
        {
            Logger.Error(e.Message);
        }
    }

    public static void Sanitize(Map map)
    {
        string sanitizedTitle = Util.String.SanitizeZalgo(map.PrettyTitle);
        string sanitizedMappers = Util.String.SanitizeZalgo(map.PrettyMappers);
        string sanitizedDiffName = Util.String.SanitizeZalgo(map.DifficultyName);

        bool updated = sanitizedTitle != map.PrettyTitle || sanitizedMappers != map.PrettyMappers || sanitizedDiffName != map.DifficultyName;

        if (updated)
        {
            map.PrettyTitle = sanitizedTitle;
            map.PrettyMappers = sanitizedMappers;
            map.DifficultyName = sanitizedDiffName;

            Update(map);
            Logger.Log($"Sanitized map {map.Name}");
        }
    }

    public static string MapsFolder => $"{Constants.USER_FOLDER}/maps";
}
