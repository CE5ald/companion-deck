using System.Runtime.InteropServices;
using System.Text;

namespace CompanionDeck.Host.Notify;

/// <summary>
/// Just enough of SQLite to read one database, through the copy that ships inside Windows
/// (winsqlite3.dll). Nothing is downloaded or installed, and nothing here can write: the file
/// is opened read-only.
/// </summary>
internal sealed class MiniSqlite : IDisposable
{
    private const string Lib = "winsqlite3.dll";
    private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x01;
    private const int SQLITE_NULL = 5, SQLITE_BLOB = 4;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    private IntPtr _db;

    public string? Error { get; private set; }

    public static MiniSqlite? OpenReadOnly(string path, out string? error)
    {
        error = null;
        try
        {
            int rc = sqlite3_open_v2(path, out var db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                error = $"open failed ({rc}): {Marshal.PtrToStringUTF8(sqlite3_errmsg(db))}";
                if (db != IntPtr.Zero) sqlite3_close(db);
                return null;
            }
            return new MiniSqlite { _db = db };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "the SQLite library that ships with Windows was not found";
            return null;
        }
    }

    /// <summary>Runs a query and hands each row to the callback as raw column values.</summary>
    public bool Query(string sql, Action<Row> each)
    {
        Error = null;
        int rc = sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            Error = $"prepare failed ({rc}): {Marshal.PtrToStringUTF8(sqlite3_errmsg(_db))}";
            return false;
        }
        try
        {
            while ((rc = sqlite3_step(stmt)) == SQLITE_ROW) each(new Row(stmt));
            if (rc != SQLITE_DONE)
            {
                Error = $"read failed ({rc}): {Marshal.PtrToStringUTF8(sqlite3_errmsg(_db))}";
                return false;
            }
            return true;
        }
        finally { sqlite3_finalize(stmt); }
    }

    public readonly struct Row
    {
        private readonly IntPtr _stmt;
        internal Row(IntPtr stmt) => _stmt = stmt;

        public int Columns => sqlite3_column_count(_stmt);
        public bool IsNull(int col) => sqlite3_column_type(_stmt, col) == SQLITE_NULL;
        public long Int(int col) => sqlite3_column_int64(_stmt, col);
        public string? Text(int col) =>
            IsNull(col) ? null : Marshal.PtrToStringUTF8(sqlite3_column_text(_stmt, col), sqlite3_column_bytes(_stmt, col));

        public byte[]? Blob(int col)
        {
            if (IsNull(col)) return null;
            int n = sqlite3_column_bytes(_stmt, col);
            var bytes = new byte[n];
            if (n > 0) Marshal.Copy(sqlite3_column_blob(_stmt, col), bytes, 0, n);
            return bytes;
        }
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero) { sqlite3_close(_db); _db = IntPtr.Zero; }
    }
}
