using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReviewTool.Core.Reviews;

/// <summary>Persistence for reviews.</summary>
public interface IReviewStore
{
    Task<IReadOnlyList<Review>> ListAsync(CancellationToken ct = default);
    Task<Review?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(Review review, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Loads, mutates and saves a review while holding a per-review lock.</summary>
    Task<Review?> UpdateAsync(string id, Func<Review, Task> mutate, CancellationToken ct = default);
}

/// <summary>
/// Stores each review as <c>{dataDir}/reviews/{id}.json</c>. Writes go to a temp file
/// first and are then moved into place so a crash never leaves a half-written review.
/// </summary>
public sealed partial class JsonFileReviewStore : IReviewStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public string Directory => _dir;

    public JsonFileReviewStore(string dataDirectory)
    {
        _dir = Path.Combine(Path.GetFullPath(dataDirectory), "reviews");
        System.IO.Directory.CreateDirectory(_dir);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string id) => IdRegex().IsMatch(id);

    private string FileFor(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid review id.", nameof(id));
        return Path.Combine(_dir, id + ".json");
    }

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task<IReadOnlyList<Review>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<Review>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id)) continue;
            try
            {
                var review = await ReadAsync(file, ct);
                if (review != null) result.Add(review);
            }
            catch (JsonException)
            {
                // Skip corrupt files rather than failing the whole listing.
            }
        }
        return result.OrderByDescending(r => r.UpdatedAt).ToList();
    }

    public async Task<Review?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return null;
        var file = FileFor(id);
        return File.Exists(file) ? await ReadAsync(file, ct) : null;
    }

    public async Task SaveAsync(Review review, CancellationToken ct = default)
    {
        var gate = LockFor(review.Id);
        await gate.WaitAsync(ct);
        try { await WriteAsync(review, ct); }
        finally { gate.Release(); }
    }

    public async Task<Review?> UpdateAsync(string id, Func<Review, Task> mutate, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return null;
        var gate = LockFor(id);
        await gate.WaitAsync(ct);
        try
        {
            var file = FileFor(id);
            if (!File.Exists(file)) return null;
            var review = await ReadAsync(file, ct);
            if (review == null) return null;
            await mutate(review);
            review.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteAsync(review, ct);
            return review;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return false;
        var gate = LockFor(id);
        await gate.WaitAsync(ct);
        try
        {
            var file = FileFor(id);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
        finally { gate.Release(); }
    }

    private static async Task<Review?> ReadAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<Review>(stream, JsonOptions, ct);
    }

    private async Task WriteAsync(Review review, CancellationToken ct)
    {
        var file = FileFor(review.Id);
        var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, review, JsonOptions, ct);
        }
        File.Move(tmp, file, overwrite: true);
    }
}
