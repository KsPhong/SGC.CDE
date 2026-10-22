using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BIMUP.CDE.Models;

namespace BIMUP.CDE.Services;

public class AccApiClient
{
    private const string BaseUrl = "https://developer.api.autodesk.com";
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _projectAdminAutodeskIds = new(StringComparer.OrdinalIgnoreCase);
    private string _clientId = "";
    private string _clientSecret = "";
    private string? _accessToken;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public AccApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public void Configure(string clientId, string clientSecret)
    {
        _clientId = clientId.Trim();
        _clientSecret = clientSecret.Trim();
    }

    public static string CleanId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        return id.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? id.Substring(2) : id;
    }

    public async Task<string> AuthenticateTwoLeggedAsync()
    {
        var url = $"{BaseUrl}/authentication/v2/token";
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "data:read data:write account:read account:write viewables:read"
        });

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi xác thực 2-Legged ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        return _accessToken ?? "";
    }

    public async Task<string> AuthenticateThreeLeggedAsync(string callbackUrl, CancellationToken ct = default)
    {
        var uri = new Uri(callbackUrl);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"{uri.Scheme}://{uri.Authority}/");
        listener.Start();

        var scope = Uri.EscapeDataString("data:read data:write account:read viewables:read");
        var redirectEncoded = Uri.EscapeDataString(callbackUrl);
        var authUrl = $"{BaseUrl}/authentication/v2/authorize?response_type=code&client_id={Uri.EscapeDataString(_clientId)}&redirect_uri={redirectEncoded}&scope={scope}";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        using var reg = ct.Register(() => listener.Stop());
        var context = await contextTask;

        var code = context.Request.QueryString["code"];
        var html = "<html><head><meta charset='utf-8'/></head><body style='font-family:Segoe UI;text-align:center;padding-top:60px;'><h2>✅ Đăng nhập SGC CDE thành công!</h2><p>Anh có thể đóng tab trình duyệt này và quay lại phần mềm SGC CDE.</p></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct);
        context.Response.OutputStream.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Không nhận được mã ủy quyền (code) từ Autodesk.");

        var tokenUrl = $"{BaseUrl}/authentication/v2/token";
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = callbackUrl
        });

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi đổi mã Token 3-Legged ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        return _accessToken ?? "";
    }

    private HttpRequestMessage CreateGet(string url, string? xUserId = null)
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Chưa xác thực API. Vui lòng bấm Kết nối trước.");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (!string.IsNullOrWhiteSpace(xUserId))
            req.Headers.TryAddWithoutValidation("x-user-id", xUserId);
        return req;
    }

    /// <summary>
    /// Tự động lấy AutodeskId của Project Admin trong dự án để gắn vào header x-user-id khi dùng 2-Legged OAuth.
    /// </summary>
    public async Task<string?> ResolveProjectAdminAutodeskIdAsync(string projectId, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        if (_projectAdminAutodeskIds.TryGetValue(cleanProjId, out var cached) && !string.IsNullOrEmpty(cached))
            return cached;

        try
        {
            var members = await GetProjectMembersAsync(cleanProjId, ct);
            var admin = members.FirstOrDefault(m =>
                            !string.IsNullOrEmpty(m.AutodeskId) &&
                            m.AccessLevel.Contains("Admin", StringComparison.OrdinalIgnoreCase))
                        ?? members.FirstOrDefault(m => !string.IsNullOrEmpty(m.AutodeskId));

            if (admin != null && !string.IsNullOrEmpty(admin.AutodeskId))
            {
                _projectAdminAutodeskIds[cleanProjId] = admin.AutodeskId;
                return admin.AutodeskId;
            }
        }
        catch
        {
            // Ignore fallback error
        }

        return null;
    }

    public async Task<List<AccHub>> GetHubsAsync()
    {
        using var req = CreateGet($"{BaseUrl}/project/v1/hubs");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi lấy danh sách Hubs ({(int)resp.StatusCode}): {body}");

        var list = new List<AccHub>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in data.EnumerateArray())
            {
                var id = h.GetProperty("id").GetString() ?? "";
                var attrs = h.GetProperty("attributes");
                var name = attrs.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                var region = attrs.TryGetProperty("region", out var r) ? r.GetString() ?? "US" : "US";
                list.Add(new AccHub { Id = id, Name = name, Region = region });
            }
        }
        return list;
    }

    public async Task<List<AccProject>> GetProjectsAsync(string hubId)
    {
        using var req = CreateGet($"{BaseUrl}/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi lấy danh sách Projects ({(int)resp.StatusCode}): {body}");

        var list = new List<AccProject>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in data.EnumerateArray())
            {
                var id = p.GetProperty("id").GetString() ?? "";
                var attrs = p.GetProperty("attributes");
                var name = attrs.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                var projType = "ACC";
                if (attrs.TryGetProperty("extension", out var ext) &&
                    ext.TryGetProperty("data", out var extData) &&
                    extData.TryGetProperty("projectType", out var pt))
                {
                    projType = pt.GetString() ?? "ACC";
                }
                list.Add(new AccProject { Id = id, Name = name, ProjectType = projType });
            }
        }
        return list;
    }

    public async Task<List<AccFolder>> GetTopFoldersAsync(string hubId, string projectId)
    {
        var url = $"{BaseUrl}/project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects/{Uri.EscapeDataString(projectId)}/topFolders";
        using var req = CreateGet(url);
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi lấy Top Folders ({(int)resp.StatusCode}): {body}");

        var allTop = new List<AccFolder>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in data.EnumerateArray())
            {
                var folder = ParseFolderElement(f, "");
                if (!folder.Hidden)
                {
                    allTop.Add(folder);
                }
            }
        }

        var userDocsRoots = allTop
            .Where(f => f.Name.Equals("Project Files", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Equals("Plans", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return userDocsRoots.Count > 0 ? userDocsRoots : allTop;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ROLES, MEMBERS, COMPANIES & FOLDER PERMISSIONS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lấy toàn bộ danh sách Role (Vị trí/Chức danh) trong dự án từ HQ v2 API.
    /// </summary>
    public async Task<List<AccRole>> GetProjectRolesAsync(string hubId, string projectId, CancellationToken ct = default)
    {
        var cleanAccId = CleanId(hubId);
        var cleanProjId = CleanId(projectId);
        var url = $"{BaseUrl}/hq/v2/accounts/{Uri.EscapeDataString(cleanAccId)}/projects/{Uri.EscapeDataString(cleanProjId)}/industry_roles";

        using var req = CreateGet(url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi lấy danh sách Roles ({(int)resp.StatusCode}): {body}");

        var roles = new List<AccRole>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = r.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var mgId = r.TryGetProperty("member_group_id", out var mgEl) ? mgEl.GetString() ?? "" : "";

                var svcs = new List<string>();
                if (r.TryGetProperty("services", out var sEl) && sEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sEl.EnumerateObject())
                    {
                        var access = prop.Value.TryGetProperty("access_level", out var al) ? al.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(access) && !access.Equals("no_access", StringComparison.OrdinalIgnoreCase))
                            svcs.Add($"{prop.Name}: {access}");
                    }
                }

                roles.Add(new AccRole
                {
                    Id = id,
                    Name = name,
                    MemberGroupId = mgId,
                    ServicesSummary = string.Join(" | ", svcs)
                });
            }
        }

        return roles.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Lấy danh sách Thành viên (Project Users) trong dự án từ Construction Admin v1 API.
    /// </summary>
    public async Task<List<AccMember>> GetProjectMembersAsync(string projectId, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var members = new List<AccMember>();
        string? url = $"{BaseUrl}/construction/admin/v1/projects/{Uri.EscapeDataString(cleanProjId)}/users?limit=200";

        while (!string.IsNullOrEmpty(url))
        {
            ct.ThrowIfCancellationRequested();
            using var req = CreateGet(url);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Lỗi lấy danh sách Thành viên dự án ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in results.EnumerateArray())
                {
                    var id = u.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = u.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var email = u.TryGetProperty("email", out var emEl) ? emEl.GetString() ?? "" : "";
                    var company = u.TryGetProperty("companyName", out var cnEl) ? cnEl.GetString() ?? "" : "";
                    var status = u.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
                    var autodeskId = u.TryGetProperty("autodeskId", out var aidEl) ? aidEl.GetString() ?? "" : "";
                    var addedOn = u.TryGetProperty("addedOn", out var aoEl) && aoEl.ValueKind == JsonValueKind.String
                        ? FormatIsoDate(aoEl.GetString()!) : "";

                    var roleNames = new List<string>();
                    if (u.TryGetProperty("roles", out var rArr) && rArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rArr.EnumerateArray())
                        {
                            if (r.TryGetProperty("name", out var rn) && rn.GetString() is string rName && !string.IsNullOrEmpty(rName))
                                roleNames.Add(rName);
                        }
                    }

                    var accessLevels = new List<string>();
                    if (u.TryGetProperty("accessLevels", out var alObj) && alObj.ValueKind == JsonValueKind.Object)
                    {
                        if (alObj.TryGetProperty("accountAdmin", out var aa) && aa.ValueKind == JsonValueKind.True)
                            accessLevels.Add("Account Admin");
                        if (alObj.TryGetProperty("projectAdmin", out var pa) && pa.ValueKind == JsonValueKind.True)
                            accessLevels.Add("Project Admin");
                        if (alObj.TryGetProperty("executive", out var ex) && ex.ValueKind == JsonValueKind.True)
                            accessLevels.Add("Executive");
                    }
                    if (accessLevels.Count == 0)
                        accessLevels.Add("Project Member");

                    var prods = new List<string>();
                    if (u.TryGetProperty("products", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pArr.EnumerateArray())
                        {
                            var key = p.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
                            var access = p.TryGetProperty("access", out var aEl) ? aEl.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(key) && !access.Equals("none", StringComparison.OrdinalIgnoreCase))
                                prods.Add($"{key} ({access})");
                        }
                    }

                    var member = new AccMember
                    {
                        Id = id,
                        Name = name,
                        Email = email,
                        CompanyName = company,
                        RolesSummary = string.Join(", ", roleNames),
                        AccessLevel = string.Join(", ", accessLevels),
                        Status = status,
                        ProductsSummary = string.Join(" | ", prods),
                        AutodeskId = autodeskId,
                        AddedOn = addedOn,
                        Scope = "Dự án"
                    };
                    members.Add(member);

                    if (!string.IsNullOrEmpty(autodeskId) &&
                        !_projectAdminAutodeskIds.ContainsKey(cleanProjId) &&
                        member.AccessLevel.Contains("Admin", StringComparison.OrdinalIgnoreCase))
                    {
                        _projectAdminAutodeskIds[cleanProjId] = autodeskId;
                    }
                }
            }

            url = null;
            if (root.TryGetProperty("pagination", out var pag) &&
                pag.TryGetProperty("nextUrl", out var nextEl) &&
                nextEl.ValueKind == JsonValueKind.String)
            {
                var nextUrl = nextEl.GetString();
                if (!string.IsNullOrWhiteSpace(nextUrl))
                    url = nextUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? nextUrl : $"{BaseUrl}{nextUrl}";
            }
        }

        if (!_projectAdminAutodeskIds.ContainsKey(cleanProjId))
        {
            var firstWithAid = members.FirstOrDefault(m => !string.IsNullOrEmpty(m.AutodeskId));
            if (firstWithAid != null)
                _projectAdminAutodeskIds[cleanProjId] = firstWithAid.AutodeskId;
        }

        return members;
    }

    /// <summary>
    /// Lấy toàn bộ danh bạ Thành viên trên ACC Account (Hub) từ HQ v1 API.
    /// </summary>
    public async Task<List<AccMember>> GetAccountUsersAsync(string hubId, CancellationToken ct = default)
    {
        var cleanAccId = CleanId(hubId);
        var url = $"{BaseUrl}/hq/v1/accounts/{Uri.EscapeDataString(cleanAccId)}/users?limit=100";
        using var req = CreateGet(url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new List<AccMember>();

        var list = new List<AccMember>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in doc.RootElement.EnumerateArray())
            {
                var id = u.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = u.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var email = u.TryGetProperty("email", out var emEl) ? emEl.GetString() ?? "" : "";
                var company = u.TryGetProperty("company_name", out var cnEl) ? cnEl.GetString() ?? "" : "";
                var defRole = u.TryGetProperty("default_role", out var drEl) ? drEl.GetString() ?? "" : "";
                var role = u.TryGetProperty("role", out var rEl) ? rEl.GetString() ?? "" : "";
                var status = u.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
                var uid = u.TryGetProperty("uid", out var uidEl) ? uidEl.GetString() ?? "" : "";
                var createdAt = u.TryGetProperty("created_at", out var caEl) && caEl.ValueKind == JsonValueKind.String
                    ? FormatIsoDate(caEl.GetString()!) : "";

                list.Add(new AccMember
                {
                    Id = id,
                    Name = name,
                    Email = email,
                    CompanyName = company,
                    RolesSummary = defRole,
                    AccessLevel = role,
                    Status = status,
                    AutodeskId = uid,
                    AddedOn = createdAt,
                    Scope = "Toàn Hub"
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Lấy danh sách các Công ty (Companies) trên ACC Account (Hub).
    /// </summary>
    public async Task<List<AccCompany>> GetAccountCompaniesAsync(string hubId, CancellationToken ct = default)
    {
        var cleanAccId = CleanId(hubId);
        var url = $"{BaseUrl}/hq/v1/accounts/{Uri.EscapeDataString(cleanAccId)}/companies?limit=100";
        using var req = CreateGet(url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new List<AccCompany>();

        var list = new List<AccCompany>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                list.Add(new AccCompany
                {
                    Id = c.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    Name = c.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "",
                    Trade = c.TryGetProperty("trade", out var trEl) ? trEl.GetString() ?? "" : "",
                    City = c.TryGetProperty("city", out var ctEl) ? ctEl.GetString() ?? "" : "",
                    Country = c.TryGetProperty("country", out var coEl) ? coEl.GetString() ?? "" : ""
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Lấy danh sách Phân quyền (Permissions) của một Thư mục trên ACC Docs.
    /// </summary>
    public async Task<List<AccPermission>> GetFolderPermissionsAsync(string projectId, AccFolder folder, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var url = $"{BaseUrl}/bim360/docs/v1/projects/{Uri.EscapeDataString(cleanProjId)}/folders/{Uri.EscapeDataString(folder.Id)}/permissions";

        using var req = CreateGet(url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new List<AccPermission>();

        var perms = new List<AccPermission>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var subjectId = p.TryGetProperty("subjectId", out var sidEl) ? sidEl.GetString() ?? "" : "";
                var subjectType = p.TryGetProperty("subjectType", out var stEl) ? stEl.GetString() ?? "" : "";
                var name = p.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var email = p.TryGetProperty("email", out var emEl) ? emEl.GetString() ?? "" : "";
                var userType = p.TryGetProperty("userType", out var utEl) ? utEl.GetString() ?? "" : "";

                var actions = ReadStringArray(p, "actions");
                var inheritActions = ReadStringArray(p, "inheritActions");
                var allActions = actions.Union(inheritActions, StringComparer.OrdinalIgnoreCase).ToList();

                string level = "Xem (View)";
                if (allActions.Contains("CONTROL"))
                    level = "Toàn quyền (Manage / Control)";
                else if (allActions.Contains("EDIT") || allActions.Contains("COLLABORATE"))
                    level = "Chỉnh sửa (Edit / Upload)";
                else if (allActions.Contains("PUBLISH"))
                    level = "Tải lên (Create / Upload)";
                else if (allActions.Contains("VIEW") && allActions.Contains("DOWNLOAD"))
                    level = "Xem + Tải về (View + Download)";

                perms.Add(new AccPermission
                {
                    FolderPath = folder.FullPath,
                    SubjectType = subjectType,
                    SubjectName = name,
                    EmailOrInfo = !string.IsNullOrEmpty(email) ? email : userType,
                    PermissionLevel = level,
                    ActionsSummary = string.Join(", ", actions),
                    InheritActionsSummary = string.Join(", ", inheritActions),
                    SubjectId = subjectId
                });
            }
        }
        return perms;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WORKFLOWS (QUY TRÌNH PHÊ DUYỆT), REVIEWS (ĐỢT DUYỆT), TRANSMITTALS & ISSUES
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly HashSet<string> ObsoleteWorkflowIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1b470d37-2723-4843-be77-03ef77b450ec", // 5001. NỘP MÔ HÌNH BIM (bản cũ)
        "04613a26-a83b-469b-a413-fe3b8ad2a437", // 5001. NỘP MÔ HÌNH BIM - PHÒNG BIM (bản cũ)
        "146a7419-5ce2-4d4f-b529-52b3a3cb1c52", // 5001. NỘP MÔ HÌNH BIM - SUBMIT BIM MODEL (bản cũ)
        "3df0f040-a886-4a74-816b-7630b337041b", // 5002. PHÁT HÀNH MÔ HÌNH BIM (bản cũ)
        "4c2c1ca4-6c0f-4aff-a857-cca8bda5c4ac", // 5002. PHÁT HÀNH MÔ HÌNH BIM - PHÒNG BIM (bản cũ)
        "46df1a38-2da6-4733-ab82-b76ee8601f29"  // 5002. PHÁT HÀNH MÔ HÌNH BIM - PUBLISH BIM MODEL (bản cũ)
    };

    /// <summary>
    /// Lấy danh sách tất cả các Quy trình phê duyệt hồ sơ (Approval Workflows) của dự án.
    /// </summary>
    public async Task<List<AccWorkflow>> GetApprovalWorkflowsAsync(string projectId, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var xUserId = await ResolveProjectAdminAutodeskIdAsync(cleanProjId, ct);
        string? url = $"{BaseUrl}/construction/reviews/v1/projects/{Uri.EscapeDataString(cleanProjId)}/workflows?limit=50";

        var list = new List<AccWorkflow>();
        while (!string.IsNullOrEmpty(url))
        {
            ct.ThrowIfCancellationRequested();
            using var req = CreateGet(url, xUserId);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Lỗi lấy danh sách Quy trình (Workflows) ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in results.EnumerateArray())
                {
                    var id = w.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = w.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var status = w.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "ACTIVE" : "ACTIVE";
                    if (!status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("TEST.", StringComparison.OrdinalIgnoreCase) ||
                        ObsoleteWorkflowIds.Contains(id))
                        continue;

                    var desc = w.TryGetProperty("description", out var dEl) ? (dEl.GetString() ?? "").Replace("\r\n", " ").Replace("\n", " ") : "";
                    var createdAt = w.TryGetProperty("createdAt", out var caEl) && caEl.ValueKind == JsonValueKind.String
                        ? FormatIsoDate(caEl.GetString()!) : "";
                    var updatedAt = w.TryGetProperty("updatedAt", out var uaEl) && uaEl.ValueKind == JsonValueKind.String
                        ? FormatIsoDate(uaEl.GetString()!) : "";

                    var stepsDesc = new List<string>();
                    var candidatesDesc = new List<string>();
                    var stepNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var stepItems = new List<AccWorkflowStepItem>();
                    int stepCount = 0;

                    if (w.TryGetProperty("steps", out var stepsArr) && stepsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var step in stepsArr.EnumerateArray())
                        {
                            stepCount++;
                            var sId = step.TryGetProperty("id", out var sidEl) ? sidEl.GetString() ?? "" : "";
                            var sName = step.TryGetProperty("name", out var snEl) ? snEl.GetString() ?? "" : "";
                            var sType = step.TryGetProperty("type", out var stpEl) ? stpEl.GetString() ?? "" : "";
                            var durInt = step.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                                ? durEl.GetInt32() : 0;
                            var dur = durInt > 0 ? $"{durInt}d" : "";
                            var dueType = step.TryGetProperty("dueDateType", out var dtEl) ? dtEl.GetString() ?? "CALENDAR_DAY" : "CALENDAR_DAY";
                            var stepLabel = string.IsNullOrEmpty(dur) ? $"{sName} ({sType})" : $"{sName} ({sType}, {dur})";
                            stepsDesc.Add(stepLabel);
                            if (!string.IsNullOrEmpty(sId))
                                stepNamesById[sId] = stepLabel;

                            var stepObj = new AccWorkflowStepItem
                            {
                                Order = stepCount,
                                StepId = sId,
                                Name = sName,
                                Type = sType,
                                Duration = durInt,
                                DueDateType = dueType
                            };

                            if (step.TryGetProperty("candidates", out var candObj) && candObj.ValueKind == JsonValueKind.Object)
                            {
                                if (candObj.TryGetProperty("roles", out var rArr) && rArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var r in rArr.EnumerateArray())
                                        if (r.TryGetProperty("name", out var rn) && rn.GetString() is string rns && !string.IsNullOrEmpty(rns))
                                            stepObj.Roles.Add(rns);
                                }
                                if (candObj.TryGetProperty("users", out var uArr) && uArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var u in uArr.EnumerateArray())
                                        if (u.TryGetProperty("name", out var un) && un.GetString() is string uns && !string.IsNullOrEmpty(uns))
                                            stepObj.Users.Add(uns);
                                }

                                var stepCands = ExtractCandidatesNames(candObj);
                                if (stepCands.Count > 0)
                                    candidatesDesc.Add($"{sName}: [{string.Join(", ", stepCands)}]");
                            }
                            stepItems.Add(stepObj);
                        }
                    }

                    var approvalOpts = new List<string>();
                    if (w.TryGetProperty("approvalStatusOptions", out var aoArr) && aoArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var opt in aoArr.EnumerateArray())
                        {
                            var label = opt.TryGetProperty("label", out var lbEl) ? lbEl.GetString() ?? "" : "";
                            var val = opt.TryGetProperty("value", out var vlEl) ? vlEl.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(label))
                                approvalOpts.Add($"{label} ({val})");
                        }
                    }

                    var copyInfo = "Không";
                    var targetFolderUrn = "";
                    bool isCopyEnabled = false;
                    if (w.TryGetProperty("copyFilesOptions", out var cfObj) && cfObj.ValueKind == JsonValueKind.Object)
                    {
                        var enabled = cfObj.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;
                        var fUrn = cfObj.TryGetProperty("folderUrn", out var fuEl) ? fuEl.GetString() ?? "" : "";
                        targetFolderUrn = fUrn;
                        isCopyEnabled = enabled;
                        if (enabled)
                            copyInfo = string.IsNullOrEmpty(fUrn) ? "Có copy" : $"Có ({fUrn})";
                    }

                    var createdBy = "Hệ thống CDE";
                    if (w.TryGetProperty("createdBy", out var cbObj))
                    {
                        if (cbObj.ValueKind == JsonValueKind.Object && cbObj.TryGetProperty("name", out var cbn))
                            createdBy = cbn.GetString() ?? "Hệ thống CDE";
                        else if (cbObj.ValueKind == JsonValueKind.String)
                            createdBy = cbObj.GetString() ?? "Hệ thống CDE";
                    }

                    var wfItem = new AccWorkflow
                    {
                        Id = id,
                        Name = name,
                        Description = desc,
                        Status = status,
                        StepCount = stepCount,
                        StepsSummary = string.Join(" ➔ ", stepsDesc),
                        CandidatesSummary = string.Join(" | ", candidatesDesc),
                        ApprovalOptionsSummary = string.Join(", ", approvalOpts),
                        CopyFilesInfo = copyInfo,
                        CopyTargetFolderUrn = targetFolderUrn,
                        CopyFilesEnabled = isCopyEnabled,
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt,
                        CreatedBy = createdBy,
                        Steps = stepItems
                    };
                    foreach (var kv in stepNamesById)
                        wfItem.StepNamesById[kv.Key] = kv.Value;

                    list.Add(wfItem);
                }
            }

            url = null;
            if (root.TryGetProperty("pagination", out var pag) &&
                pag.TryGetProperty("nextUrl", out var nextEl) &&
                nextEl.ValueKind == JsonValueKind.String)
            {
                var nextUrl = nextEl.GetString();
                if (!string.IsNullOrWhiteSpace(nextUrl))
                    url = nextUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? nextUrl : $"{BaseUrl}{nextUrl}";
            }
        }

        return list.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Lấy danh sách tất cả các Đợt trình duyệt hồ sơ (Reviews) trong dự án kèm danh sách file trong mỗi đợt.
    /// </summary>
    public async Task<List<AccReview>> GetProjectReviewsAsync(
        string projectId,
        List<AccWorkflow>? workflows = null,
        CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var xUserId = await ResolveProjectAdminAutodeskIdAsync(cleanProjId, ct);
        var url = $"{BaseUrl}/construction/reviews/v1/projects/{Uri.EscapeDataString(cleanProjId)}/reviews?limit=50";

        using var req = CreateGet(url, xUserId);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi lấy danh sách Đợt trình duyệt (Reviews) ({(int)resp.StatusCode}): {body}");

        var wfObjMap = workflows?.ToDictionary(w => w.Id, w => w, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, AccWorkflow>(StringComparer.OrdinalIgnoreCase);

        var list = new List<AccReview>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var seqId = r.TryGetProperty("sequenceId", out var sqEl) && sqEl.ValueKind == JsonValueKind.Number ? sqEl.GetInt32() : 0;
                var name = r.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var status = r.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
                var wfId = r.TryGetProperty("workflowId", out var wfEl) ? wfEl.GetString() ?? "" : "";
                var stepId = r.TryGetProperty("currentStepId", out var csEl) ? csEl.GetString() ?? "" : "";
                var dueDate = r.TryGetProperty("currentStepDueDate", out var ddEl) && ddEl.ValueKind == JsonValueKind.String
                    ? FormatIsoDate(ddEl.GetString()!) : "";
                var createdAt = r.TryGetProperty("createdAt", out var caEl) && caEl.ValueKind == JsonValueKind.String
                    ? FormatIsoDate(caEl.GetString()!) : "";
                var finishedAt = r.TryGetProperty("finishedAt", out var faEl) && faEl.ValueKind == JsonValueKind.String
                    ? FormatIsoDate(faEl.GetString()!) : "—";

                var createdBy = "";
                if (r.TryGetProperty("createdBy", out var cbObj) && cbObj.ValueKind == JsonValueKind.Object)
                {
                    createdBy = cbObj.TryGetProperty("name", out var cbn) ? cbn.GetString() ?? "" : "";
                }

                var nextActors = new List<string>();
                if (r.TryGetProperty("nextActionBy", out var naObj) && naObj.ValueKind == JsonValueKind.Object)
                {
                    if (naObj.TryGetProperty("candidates", out var candObj) && candObj.ValueKind == JsonValueKind.Object)
                    {
                        nextActors.AddRange(ExtractCandidatesNames(candObj));
                    }
                }

                var wfName = wfObjMap.TryGetValue(wfId, out var matchedWf) ? matchedWf.Name : wfId;
                var stepDisplay = (matchedWf != null && matchedWf.StepNamesById.TryGetValue(stepId, out var resolvedStep))
                    ? resolvedStep
                    : (string.IsNullOrEmpty(stepId) ? "Hoàn tất" : stepId);

                var review = new AccReview
                {
                    Id = id,
                    SequenceId = seqId,
                    Name = name,
                    WorkflowId = wfId,
                    WorkflowName = wfName,
                    Status = status,
                    CurrentStepName = stepDisplay,
                    CurrentStepDueDate = dueDate,
                    NextActionBy = string.Join(", ", nextActors),
                    CreatedBy = createdBy,
                    CreatedAt = createdAt,
                    FinishedAt = finishedAt
                };

                // Lấy thêm danh sách file bên trong đợt Review này
                try
                {
                    var verUrl = $"{BaseUrl}/construction/reviews/v1/projects/{Uri.EscapeDataString(cleanProjId)}/reviews/{Uri.EscapeDataString(id)}/versions?limit=50";
                    using var verReq = CreateGet(verUrl, xUserId);
                    using var verResp = await _http.SendAsync(verReq, ct);
                    if (verResp.IsSuccessStatusCode)
                    {
                        var verBody = await verResp.Content.ReadAsStringAsync(ct);
                        using var verDoc = JsonDocument.Parse(verBody);
                        if (verDoc.RootElement.TryGetProperty("results", out var verArr) && verArr.ValueKind == JsonValueKind.Array)
                        {
                            var fileNames = new List<string>();
                            foreach (var fv in verArr.EnumerateArray())
                            {
                                var fn = fv.TryGetProperty("name", out var fnEl) ? fnEl.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(fn))
                                    fileNames.Add(fn);
                            }
                            review.FilesCount = fileNames.Count;
                            review.FilesSummary = string.Join(" | ", fileNames);
                        }
                    }
                }
                catch
                {
                    // Ignore per-review version error
                }

                list.Add(review);
            }
        }

        return list;
    }

    /// <summary>
    /// Lấy danh sách Hồ sơ phát hành (Transmittals) của dự án.
    /// </summary>
    public async Task<List<AccTransmittal>> GetProjectTransmittalsAsync(string projectId, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var xUserId = await ResolveProjectAdminAutodeskIdAsync(cleanProjId, ct);
        var url = $"{BaseUrl}/construction/transmittals/v1/projects/{Uri.EscapeDataString(cleanProjId)}/transmittals?limit=100";

        using var req = CreateGet(url, xUserId);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return new List<AccTransmittal>();

        var body = await resp.Content.ReadAsStringAsync(ct);
        var list = new List<AccTransmittal>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in results.EnumerateArray())
            {
                list.Add(new AccTransmittal
                {
                    Id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    SequenceId = t.TryGetProperty("sequenceId", out var sqEl) && sqEl.ValueKind == JsonValueKind.Number ? sqEl.GetInt32() : 0,
                    Title = t.TryGetProperty("title", out var ttEl) ? ttEl.GetString() ?? "" : "",
                    Status = t.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "",
                    DocsCount = t.TryGetProperty("docsCount", out var dcEl) && dcEl.ValueKind == JsonValueKind.Number ? dcEl.GetInt32() : 0,
                    CreatedBy = t.TryGetProperty("sentBy", out var sbObj) && sbObj.ValueKind == JsonValueKind.Object && sbObj.TryGetProperty("name", out var sbn) ? sbn.GetString() ?? "" : "",
                    CreatedAt = t.TryGetProperty("createdAt", out var caEl) && caEl.ValueKind == JsonValueKind.String ? FormatIsoDate(caEl.GetString()!) : ""
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Lấy danh sách Issues (Vấn đề dự án) từ ACC Issues v1 API.
    /// Lưu ý: Autodesk ACC Issues API v1 yêu cầu xác thực 3-Legged OAuth (đăng nhập cá nhân).
    /// </summary>
    public async Task<(List<AccIssue> Issues, string? Note)> GetProjectIssuesAsync(string projectId, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var xUserId = await ResolveProjectAdminAutodeskIdAsync(cleanProjId, ct);
        var url = $"{BaseUrl}/construction/issues/v1/projects/{Uri.EscapeDataString(cleanProjId)}/issues?limit=100";

        using var req = CreateGet(url, xUserId);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (new List<AccIssue>(),
                    "Autodesk ACC Issues API yêu cầu xác thực 3-Legged OAuth (Đăng nhập tài khoản cá nhân qua trình duyệt ở mục 'Cấu hình API').");
            }
            return (new List<AccIssue>(), $"Lỗi gọi Issues API ({(int)resp.StatusCode}): {body}");
        }

        var list = new List<AccIssue>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var iss in results.EnumerateArray())
            {
                list.Add(new AccIssue
                {
                    Id = iss.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    DisplayId = iss.TryGetProperty("displayId", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt32() : 0,
                    Title = iss.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "",
                    Description = iss.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "",
                    Status = iss.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "",
                    IssueType = iss.TryGetProperty("issueSubtypeId", out var itEl) ? itEl.GetString() ?? "" : "",
                    AssignedTo = iss.TryGetProperty("assignedTo", out var atEl) ? atEl.GetString() ?? "" : "",
                    DueDate = iss.TryGetProperty("dueDate", out var ddEl) && ddEl.ValueKind == JsonValueKind.String ? ddEl.GetString()! : "",
                    CreatedBy = iss.TryGetProperty("createdBy", out var cbEl) ? cbEl.GetString() ?? "" : "",
                    CreatedAt = iss.TryGetProperty("createdAt", out var caEl) && caEl.ValueKind == JsonValueKind.String ? FormatIsoDate(caEl.GetString()!) : "",
                    LocationDetails = iss.TryGetProperty("locationDetails", out var ldEl) ? ldEl.GetString() ?? "" : ""
                });
            }
        }
        return (list, null);
    }

    private static List<string> ExtractCandidatesNames(JsonElement candObj)
    {
        var names = new List<string>();
        foreach (var propName in new[] { "roles", "users", "companies" })
        {
            if (candObj.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nEl) && nEl.GetString() is string nm && !string.IsNullOrEmpty(nm))
                        names.Add(nm);
                }
            }
        }
        return names;
    }

    private static List<string> ReadStringArray(JsonElement obj, string propName)
    {
        var list = new List<string>();
        if (obj.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && el.GetString() is string s && !string.IsNullOrEmpty(s))
                    list.Add(s);
            }
        }
        return list;
    }

    private static AccFolder ParseFolderElement(JsonElement f, string parentPath)
    {
        var id = f.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var attrs = f.GetProperty("attributes");
        var name = attrs.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString()!
            : (attrs.TryGetProperty("name", out var n) ? n.GetString() ?? id : id);
        var count = attrs.TryGetProperty("objectCount", out var oc) && oc.ValueKind == JsonValueKind.Number
            ? oc.GetInt32()
            : 0;
        var hidden = attrs.TryGetProperty("hidden", out var hd) && hd.ValueKind == JsonValueKind.True;
        var createUser = attrs.TryGetProperty("createUserName", out var cu) && cu.ValueKind == JsonValueKind.String
            ? cu.GetString()! : "";
        var createTime = attrs.TryGetProperty("createTime", out var ct) && ct.ValueKind == JsonValueKind.String
            ? FormatIsoDate(ct.GetString()!) : "";
        var modUser = attrs.TryGetProperty("lastModifiedUserName", out var lmu) && lmu.ValueKind == JsonValueKind.String
            ? lmu.GetString()! : "";
        var modTime = attrs.TryGetProperty("lastModifiedTime", out var lmt) && lmt.ValueKind == JsonValueKind.String
            ? FormatIsoDate(lmt.GetString()!) : "";

        var fullPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

        return new AccFolder
        {
            Id = id,
            Name = name,
            FullPath = fullPath,
            ObjectCount = count,
            Hidden = hidden,
            CreateUserName = createUser,
            CreateTime = createTime,
            LastModifiedUserName = modUser,
            LastModifiedTime = modTime
        };
    }

    public async Task<(List<AccFolder> Subfolders, List<AccFileItem> Files)> GetSingleFolderContentsAsync(
        string projectId,
        AccFolder parentFolder,
        CancellationToken ct = default)
    {
        var subfolders = new List<AccFolder>();
        var files = new List<AccFileItem>();

        string? url = $"{BaseUrl}/data/v1/projects/{Uri.EscapeDataString(projectId)}/folders/{Uri.EscapeDataString(parentFolder.Id)}/contents";

        while (!string.IsNullOrEmpty(url))
        {
            ct.ThrowIfCancellationRequested();
            using var req = CreateGet(url);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Lỗi đọc thư mục '{parentFolder.FullPath}' ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var versionsMap = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                foreach (var inc in included.EnumerateArray())
                {
                    if (inc.TryGetProperty("type", out var t) && t.GetString() == "versions" &&
                        inc.TryGetProperty("id", out var idEl) && idEl.GetString() is string vid)
                    {
                        versionsMap[vid] = inc.Clone();
                    }
                }
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var itemType = item.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                    if (itemType == "folders")
                    {
                        var sub = ParseFolderElement(item, parentFolder.FullPath);
                        if (!sub.Hidden)
                        {
                            sub.ParentId = parentFolder.Id;
                            subfolders.Add(sub);
                        }
                    }
                    else if (itemType == "items")
                    {
                        var isHidden = item.TryGetProperty("attributes", out var ia) &&
                                       ia.TryGetProperty("hidden", out var hd) &&
                                       hd.ValueKind == JsonValueKind.True;
                        if (!isHidden)
                        {
                            files.Add(ParseFileItemElement(item, parentFolder.FullPath, versionsMap));
                        }
                    }
                }
            }

            url = null;
            if (root.TryGetProperty("links", out var links) &&
                links.TryGetProperty("next", out var next) &&
                next.TryGetProperty("href", out var href) &&
                href.ValueKind == JsonValueKind.String)
            {
                url = href.GetString();
            }
        }

        return (subfolders, files);
    }

    public async Task<(List<AccFolder> AllFolders, List<AccFileItem> AllFiles)> ScanFolderTreeRecursiveAsync(
        string projectId,
        AccFolder rootFolder,
        bool includeCustomAttributes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var allFolders = new List<AccFolder> { rootFolder };
        var allFiles = new List<AccFileItem>();

        await CrawlTreeRecursiveInternalAsync(projectId, rootFolder, allFolders, allFiles, progress, ct);

        if (includeCustomAttributes && allFiles.Count > 0)
        {
            progress?.Report($"Đang lấy Custom Attributes trên ACC Docs cho {allFiles.Count} files...");
            await EnrichCustomAttributesAsync(projectId, allFiles, ct);
        }

        return (allFolders, allFiles);
    }

    private async Task CrawlTreeRecursiveInternalAsync(
        string projectId,
        AccFolder currentFolder,
        List<AccFolder> allFolders,
        List<AccFileItem> allFiles,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"📂 Đang đọc thư mục: {currentFolder.FullPath} ({allFolders.Count} folders, {allFiles.Count} files)...");
        var (subfolders, files) = await GetSingleFolderContentsAsync(projectId, currentFolder, ct);

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            currentFolder.Children.Clear();
            foreach (var sf in subfolders)
                currentFolder.Children.Add(sf);
        });

        allFolders.AddRange(subfolders);
        allFiles.AddRange(files);

        foreach (var sub in subfolders)
        {
            await CrawlTreeRecursiveInternalAsync(projectId, sub, allFolders, allFiles, progress, ct);
        }
    }

    private static AccFileItem ParseFileItemElement(
        JsonElement item,
        string currentPath,
        Dictionary<string, JsonElement> versionsMap)
    {
        var itemId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var attrs = item.GetProperty("attributes");
        var name = attrs.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString()!
            : (attrs.TryGetProperty("name", out var nm) ? nm.GetString() ?? "Unnamed" : "Unnamed");

        var tipId = "";
        if (item.TryGetProperty("relationships", out var rel) &&
            rel.TryGetProperty("tip", out var tip) &&
            tip.TryGetProperty("data", out var tipData) &&
            tipData.TryGetProperty("id", out var tipIdEl))
        {
            tipId = tipIdEl.GetString() ?? "";
        }

        var fileItem = new AccFileItem
        {
            FileName = name,
            FolderPath = currentPath,
            ItemId = itemId,
            VersionUrn = tipId,
            FileType = Path.GetExtension(name).TrimStart('.').ToUpperInvariant()
        };

        if (!string.IsNullOrEmpty(tipId) && versionsMap.TryGetValue(tipId, out var verObj))
        {
            if (verObj.TryGetProperty("attributes", out var vAttrs))
            {
                if (vAttrs.TryGetProperty("versionNumber", out var vn) && vn.ValueKind == JsonValueKind.Number)
                    fileItem.VersionNumber = vn.GetInt32();
                if (vAttrs.TryGetProperty("storageSize", out var ss) && ss.ValueKind == JsonValueKind.Number)
                {
                    fileItem.SizeBytes = ss.GetInt64();
                    fileItem.SizeMb = Math.Round(fileItem.SizeBytes / (1024.0 * 1024.0), 2);
                }
                if (vAttrs.TryGetProperty("fileType", out var ft) && ft.ValueKind == JsonValueKind.String)
                    fileItem.FileType = ft.GetString()!.ToUpperInvariant();
                if (vAttrs.TryGetProperty("createUserName", out var cu) && cu.ValueKind == JsonValueKind.String)
                    fileItem.CreateUserName = cu.GetString()!;
                if (vAttrs.TryGetProperty("createTime", out var ctEl) && ctEl.ValueKind == JsonValueKind.String)
                    fileItem.CreateTime = FormatIsoDate(ctEl.GetString()!);
                if (vAttrs.TryGetProperty("lastModifiedUserName", out var lmu) && lmu.ValueKind == JsonValueKind.String)
                    fileItem.LastModifiedUserName = lmu.GetString()!;
                if (vAttrs.TryGetProperty("lastModifiedTime", out var lmt) && lmt.ValueKind == JsonValueKind.String)
                    fileItem.LastModifiedTime = FormatIsoDate(lmt.GetString()!);

                if (vAttrs.TryGetProperty("extension", out var ext) &&
                    ext.TryGetProperty("data", out var extData) &&
                    extData.TryGetProperty("reviewState", out var rs) &&
                    rs.ValueKind == JsonValueKind.String)
                {
                    fileItem.ReviewState = rs.GetString()!;
                }
            }

            if (verObj.TryGetProperty("relationships", out var vRel))
            {
                if (vRel.TryGetProperty("storage", out var st) &&
                    st.TryGetProperty("data", out var stData) &&
                    stData.TryGetProperty("id", out var stId))
                {
                    fileItem.StorageUrn = stId.GetString() ?? "";
                }
                if (vRel.TryGetProperty("derivatives", out var der) &&
                    der.TryGetProperty("data", out var derData) &&
                    derData.TryGetProperty("id", out var derId))
                {
                    fileItem.DerivativeUrn = derId.GetString() ?? "";
                }
            }
        }

        return fileItem;
    }

    public async Task EnrichCustomAttributesAsync(string projectId, List<AccFileItem> files, CancellationToken ct = default)
    {
        var cleanProjId = CleanId(projectId);
        var url = $"{BaseUrl}/bim360/docs/v1/projects/{Uri.EscapeDataString(cleanProjId)}/versions:batch-get";

        var byUrn = files.Where(f => !string.IsNullOrEmpty(f.VersionUrn))
                         .GroupBy(f => f.VersionUrn)
                         .ToDictionary(g => g.Key, g => g.ToList());

        var allUrns = byUrn.Keys.ToList();
        for (int i = 0; i < allUrns.Count; i += 50)
        {
            ct.ThrowIfCancellationRequested();
            var batch = allUrns.Skip(i).Take(50).ToArray();
            var payload = JsonSerializer.Serialize(new { urns = batch });

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                continue;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var r in results.EnumerateArray())
            {
                var urn = r.TryGetProperty("urn", out var u) ? u.GetString() ?? "" : "";
                if (!byUrn.TryGetValue(urn, out var targetItems))
                    continue;

                if (r.TryGetProperty("customAttributes", out var caArr) && caArr.ValueKind == JsonValueKind.Array)
                {
                    var pairs = new List<string>();
                    foreach (var attr in caArr.EnumerateArray())
                    {
                        var aName = attr.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        var aVal = attr.TryGetProperty("value", out var av) ? av.ToString() : "";
                        if (!string.IsNullOrEmpty(aName) && !string.IsNullOrEmpty(aVal))
                            pairs.Add($"{aName}: {aVal}");
                    }
                    var summary = string.Join(" | ", pairs);
                    foreach (var item in targetItems)
                        item.CustomAttributesSummary = summary;
                }
            }
        }
    }

    public async Task DownloadFileAsync(string storageUrn, string localFilePath, CancellationToken ct = default)
    {
        const string marker = "os.object:";
        var idx = storageUrn.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            throw new InvalidOperationException($"File không có Storage URN hợp lệ để tải trực tiếp ({storageUrn}).");

        var raw = storageUrn.Substring(idx + marker.Length);
        var slashIdx = raw.IndexOf('/');
        if (slashIdx <= 0)
            throw new InvalidOperationException($"Định dạng Storage URN không hợp lệ: {storageUrn}");

        var bucketKey = raw.Substring(0, slashIdx);
        var objectKey = raw.Substring(slashIdx + 1);
        var signUrl = $"{BaseUrl}/oss/v2/buckets/{Uri.EscapeDataString(bucketKey)}/objects/{Uri.EscapeDataString(objectKey)}/signeds3download";

        using var req = CreateGet(signUrl);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi lấy link S3 Download ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var downloadUrl = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException("Không tìm thấy URL tải file trong phản hồi từ Autodesk OSS.");

        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var s3Resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        s3Resp.EnsureSuccessStatusCode();
        await using var netStream = await s3Resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await netStream.CopyToAsync(fileStream, ct);
    }

    public static void ExportFoldersToCsv(IEnumerable<AccFolder> folders, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tên Thư Mục,Đường Dẫn Đầy Đủ,Số Phần Tử (ObjectCount),Người Tạo,Ngày Tạo,Người Cập Nhật Cuối,Ngày Cập Nhật Cuối,Folder ID / URN");
        foreach (var f in folders)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(f.Name),
                EscapeCsv(f.FullPath),
                f.ObjectCount,
                EscapeCsv(f.CreateUserName),
                EscapeCsv(f.CreateTime),
                EscapeCsv(f.LastModifiedUserName),
                EscapeCsv(f.LastModifiedTime),
                EscapeCsv(f.Id)
            ));
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    public static void ExportToCsv(IEnumerable<AccFileItem> files, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tên File,Đường dẫn thư mục,Loại File,Version,Kích thước (MB),Người tạo,Ngày tạo,Người cập nhật cuối,Ngày cập nhật cuối,Trạng thái Review,Custom Attributes,Item ID,Version URN,Storage URN");
        foreach (var f in files)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(f.FileName),
                EscapeCsv(f.FolderPath),
                EscapeCsv(f.FileType),
                f.VersionNumber,
                f.SizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EscapeCsv(f.CreateUserName),
                EscapeCsv(f.CreateTime),
                EscapeCsv(f.LastModifiedUserName),
                EscapeCsv(f.LastModifiedTime),
                EscapeCsv(f.ReviewState),
                EscapeCsv(f.CustomAttributesSummary),
                EscapeCsv(f.ItemId),
                EscapeCsv(f.VersionUrn),
                EscapeCsv(f.StorageUrn)
            ));
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    public static void ExportMembersAndRolesToCsv(IEnumerable<AccRole> roles, IEnumerable<AccMember> members, IEnumerable<AccPermission> perms, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 1. DANH SÁCH ROLES DỰ ÁN ===");
        sb.AppendLine("Tên Role,Role ID,Member Group ID,Quyền Dịch Vụ (Services)");
        foreach (var r in roles)
        {
            sb.AppendLine(string.Join(",", EscapeCsv(r.Name), EscapeCsv(r.Id), EscapeCsv(r.MemberGroupId), EscapeCsv(r.ServicesSummary)));
        }

        sb.AppendLine();
        sb.AppendLine("=== 2. DANH SÁCH THÀNH VIÊN (MEMBERS) ===");
        sb.AppendLine("Phạm Vi,Họ và Tên,Email,Công Ty,Role Dự Án,Cấp Quyền (Access Level),Trạng Thái,Dịch Vụ (Products),Ngày Tham Gia,Autodesk ID");
        foreach (var m in members)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(m.Scope),
                EscapeCsv(m.Name),
                EscapeCsv(m.Email),
                EscapeCsv(m.CompanyName),
                EscapeCsv(m.RolesSummary),
                EscapeCsv(m.AccessLevel),
                EscapeCsv(m.Status),
                EscapeCsv(m.ProductsSummary),
                EscapeCsv(m.AddedOn),
                EscapeCsv(m.AutodeskId)));
        }

        sb.AppendLine();
        sb.AppendLine("=== 3. PHÂN QUYỀN THƯ MỤC (FOLDER PERMISSIONS) ===");
        sb.AppendLine("Đường Dẫn Thư Mục,Loại Đối Tượng,Tên User / Role / Công Ty,Email / Loại,Mức Quyền,Quyền Trực Tiếp,Quyền Kế Thừa");
        foreach (var p in perms)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(p.FolderPath),
                EscapeCsv(p.SubjectType),
                EscapeCsv(p.SubjectName),
                EscapeCsv(p.EmailOrInfo),
                EscapeCsv(p.PermissionLevel),
                EscapeCsv(p.ActionsSummary),
                EscapeCsv(p.InheritActionsSummary)));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    public static void ExportWorkflowsAndReviewsToCsv(IEnumerable<AccWorkflow> workflows, IEnumerable<AccReview> reviews, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 1. QUY TRÌNH PHÊ DUYỆT (APPROVAL WORKFLOWS) ===");
        sb.AppendLine("Tên Quy Trình,Mô Tả,Trạng Thái,Số Bước,Chi Tiết Các Bước (Steps),Các Role / Người Tham Gia (Candidates),Trạng Thái Phê Duyệt,Copy File Sau Khi Duyệt,Ngày Tạo");
        foreach (var w in workflows)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(w.Name),
                EscapeCsv(w.Description),
                EscapeCsv(w.Status),
                w.StepCount,
                EscapeCsv(w.StepsSummary),
                EscapeCsv(w.CandidatesSummary),
                EscapeCsv(w.ApprovalOptionsSummary),
                EscapeCsv(w.CopyFilesInfo),
                EscapeCsv(w.CreatedAt)));
        }

        sb.AppendLine();
        sb.AppendLine("=== 2. CÁC ĐỢT TRÌNH DUYỆT HỒ SƠ (REVIEWS) ===");
        sb.AppendLine("Mã Số (#),Tên Đợt Trình Duyệt,Quy Trình Áp Dụng,Trạng Thái,Bước Hiện Tại,Hạn Xử Lý (Due Date),Người / Role Xử Lý Tiếp Theo,Số Lượng File,Danh Sách File Đính Kèm,Người Tạo,Ngày Tạo,Ngày Hoàn Tất");
        foreach (var r in reviews)
        {
            sb.AppendLine(string.Join(",",
                r.SequenceId,
                EscapeCsv(r.Name),
                EscapeCsv(r.WorkflowName),
                EscapeCsv(r.Status),
                EscapeCsv(r.CurrentStepName),
                EscapeCsv(r.CurrentStepDueDate),
                EscapeCsv(r.NextActionBy),
                r.FilesCount,
                EscapeCsv(r.FilesSummary),
                EscapeCsv(r.CreatedBy),
                EscapeCsv(r.CreatedAt),
                EscapeCsv(r.FinishedAt)));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
    }

    private static string EscapeCsv(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "\"\"";
        return $"\"{val.Replace("\"", "\"\"")}\"";
    }

    private static string FormatIsoDate(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return iso;
    }
}
