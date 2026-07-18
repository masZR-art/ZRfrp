namespace ZRfrp.Server;

public sealed class AnnouncementService
{
    private readonly StateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AnnouncementService(StateStore store)
    {
        _store = store;
    }

    public IReadOnlyList<Announcement> ActiveAnnouncements(DateTimeOffset now) =>
        _store.State.Announcements
            .Where(item => item.Enabled
                           && item.PublishedAt <= now
                           && (item.ExpiresAt is null || item.ExpiresAt > now))
            .OrderByDescending(item => item.PublishedAt)
            .ToArray();

    public async Task<(Announcement? Announcement, string? Error)> CreateAsync(
        AnnouncementRequest request)
    {
        var error = Validate(request);
        if (error is not null) return (null, error);
        await _gate.WaitAsync();
        try
        {
            var announcement = new Announcement();
            Apply(announcement, request);
            _store.State.Announcements.Add(announcement);
            await _store.AuditAsync("announcement", $"发布公告 {announcement.Title}");
            return (announcement, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(Announcement? Announcement, string? Error)> UpdateAsync(
        string id, AnnouncementRequest request)
    {
        var error = Validate(request);
        if (error is not null) return (null, error);
        await _gate.WaitAsync();
        try
        {
            var announcement = _store.State.Announcements.FirstOrDefault(item => item.Id == id);
            if (announcement is null) return (null, "公告不存在。");
            Apply(announcement, request);
            await _store.AuditAsync("announcement", $"更新公告 {announcement.Title}");
            return (announcement, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var announcement = _store.State.Announcements.FirstOrDefault(item => item.Id == id);
            if (announcement is null) return false;
            _store.State.Announcements.Remove(announcement);
            await _store.AuditAsync("announcement", $"删除公告 {announcement.Title}");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Validate(AnnouncementRequest request)
    {
        var title = (request.Title ?? "").Trim();
        var content = (request.Content ?? "").Trim();
        if (title.Length is < 2 or > 80) return "公告标题需为 2 至 80 个字符。";
        if (content.Length is < 2 or > 4000) return "公告内容需为 2 至 4000 个字符。";
        if (request.ExpiresAt is { } expiresAt && expiresAt <= request.PublishedAt)
            return "公告失效时间必须晚于发布时间。";
        return null;
    }

    private static void Apply(Announcement announcement, AnnouncementRequest request)
    {
        announcement.Title = request.Title.Trim();
        announcement.Content = request.Content.Trim();
        announcement.Enabled = request.Enabled;
        announcement.PublishedAt = request.PublishedAt;
        announcement.ExpiresAt = request.ExpiresAt;
        announcement.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
