using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using SQLite;
using SQLitePCL;

public partial class DatabaseService : Node
{
    public static readonly string DATABASE_PATH = Constants.USER_FOLDER + "/data.db";

    public static SQLiteConnection Connection { get; set; }

    public override void _Ready()
    {
        registerAndroidSqliteResolver();
        Batteries.Init();
        Connection = new SQLiteConnection(DATABASE_PATH);
    }

    /// <summary>
    /// Android ships the SQLite native library as libe_sqlite3.so inside the APK, but neither
    /// the runtime's default probing nor a bare dlopen by soname resolves the provider's
    /// plain "e_sqlite3" name, so the connection throws DllNotFoundException.
    /// Load the real file once and answer the provider's imports with that handle.
    /// </summary>
    private static void registerAndroidSqliteResolver()
    {
        if (OS.GetName() != "Android")
        {
            return;
        }

        IntPtr handle = loadSqliteLibrary();

        if (handle == IntPtr.Zero)
        {
            GD.PushError("[Rhythia] could not load libe_sqlite3.so");
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(SQLite3Provider_e_sqlite3).Assembly,
                (name, _, _) => name.Contains("e_sqlite3") ? handle : IntPtr.Zero
            );
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for this assembly; nothing left to do.
        }
    }

    /// <summary>Tries the plain soname first, then the app's native library directory.</summary>
    private static IntPtr loadSqliteLibrary()
    {
        if (NativeLibrary.TryLoad("libe_sqlite3.so", out var handle))
        {
            GD.Print("[Rhythia] sqlite loaded by soname");
            return handle;
        }

        try
        {
            // Android only lets the process dlopen absolute paths inside its own native
            // library directory, and the engine library is mapped from exactly there.
            foreach (var line in File.ReadAllLines("/proc/self/maps"))
            {
                int pathStart = line.IndexOf('/');

                if (pathStart < 0)
                {
                    continue;
                }

                string mappedPath = line[pathStart..].Trim();

                if (!mappedPath.EndsWith("libgodot_android.so", StringComparison.Ordinal))
                {
                    continue;
                }

                string? directory = Path.GetDirectoryName(mappedPath);
                string candidate = Path.Combine(directory ?? string.Empty, "libe_sqlite3.so");

                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    GD.Print($"[Rhythia] sqlite loaded from {candidate}");
                    return handle;
                }
            }
        }
        catch (Exception exception)
        {
            GD.PushError($"[Rhythia] sqlite probe failed: {exception.Message}");
        }

        return IntPtr.Zero;
    }

    public override void _ExitTree()
    {
        Connection.Dispose();
    }
}
