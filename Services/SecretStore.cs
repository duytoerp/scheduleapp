using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Kho bí mật (mật khẩu, token…) dùng trong flow qua {{secret:Tên}}. Lưu ở secrets.json, giá trị mã hóa DPAPI,
/// không nằm trong jobs.json nên không bị lộ khi xuất/chia sẻ công việc.
/// </summary>
public static class SecretStore
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _items;

    private static string FilePath => Path.Combine(JobStore.DataDir, "secrets.json");

    private static Dictionary<string, string> Items
    {
        get
        {
            if (_items != null) return _items;
            _items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath), JsonDefaults.Options);
                    if (loaded != null)
                        foreach (var (k, v) in loaded) _items[k] = v;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Không đọc được secrets.json: " + ex.Message);
            }
            return _items;
        }
    }

    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Sync) return Items.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static bool Contains(string name)
    {
        lock (Sync) return Items.ContainsKey(name.Trim());
    }

    /// <summary>Giá trị đã giải mã, hoặc null nếu chưa có.</summary>
    public static string? Get(string name)
    {
        lock (Sync)
        {
            return Items.TryGetValue(name.Trim(), out var stored) ? Protector.Unprotect(stored) : null;
        }
    }

    public static void Set(string name, string value)
    {
        lock (Sync)
        {
            Items[name.Trim()] = Protector.Protect(value);
            Save();
        }
    }

    public static void Remove(string name)
    {
        lock (Sync)
        {
            if (Items.Remove(name.Trim())) Save();
        }
    }

    private static void Save()
    {
        Directory.CreateDirectory(JobStore.DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Items, JsonDefaults.Options));
        File.Move(tmp, FilePath, true);
    }
}
