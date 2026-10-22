using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BIMUP.CDE.Services;

/// <summary>
/// Cầu nối MCP tích hợp trực tiếp trong BIMUP.CDE (theo chuẩn giao thức BIMUP.Mcp).
/// Ghi descriptor vào %LOCALAPPDATA%\BIMUP\mcp\targets\cde-2026-{pid}.json
/// và lắng nghe lệnh qua Named Pipe BIMUP.Mcp.cde.{pid} &amp; BIMUP.Mcp.cde.
/// </summary>
public sealed class CdeMcpBridge : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly MainWindow _window;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _pid;
    private readonly string _token;
    private readonly string _pipeName;
    private readonly string _fixedPipeName = "BIMUP.Mcp.cde";
    private readonly string _descriptorPath;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private string _currentDocument = "(Chưa chọn dự án)";

    public string PipeName => _pipeName;
    public int Pid => _pid;

    public CdeMcpBridge(MainWindow window)
    {
        _window = window;
        _pid = Environment.ProcessId;
        _token = Guid.NewGuid().ToString("N");
        _pipeName = $"BIMUP.Mcp.cde.{_pid}";

        var targetsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIMUP", "mcp", "targets");
        Directory.CreateDirectory(targetsDir);
        _descriptorPath = Path.Combine(targetsDir, $"cde-2026-{_pid}.json");
    }

    public void Start()
    {
        WriteDescriptor();
        _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _ = Task.Run(() => PipeServerLoopAsync(_pipeName, requireToken: true, _cts.Token));
        _ = Task.Run(() => PipeServerLoopAsync(_fixedPipeName, requireToken: false, _cts.Token));
    }

    public void UpdateDocumentContext(string docName)
    {
        _currentDocument = string.IsNullOrWhiteSpace(docName) ? "(Chưa chọn dự án)" : docName;
        WriteDescriptor();
    }

    private void WriteDescriptor()
    {
        try
        {
            var desc = new JsonObject
            {
                ["protocol"] = 1,
                ["host"] = "cde",
                ["product"] = "BIMUP CDE Studio",
                ["year"] = 2026,
                ["pid"] = _pid,
                ["pipe"] = _pipeName,
                ["token"] = _token,
                ["bridge_version"] = "1.0.0",
                ["document"] = _currentDocument,
                ["started_utc"] = _startedUtc,
                ["heartbeat_utc"] = DateTime.UtcNow,
                ["allow_code"] = false
            };
            File.WriteAllText(_descriptorPath, desc.ToJsonString(JsonOpts), Utf8);
        }
        catch
        {
            // Ignore write race
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WriteDescriptor();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PipeServerLoopAsync(string pipeName, bool requireToken, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);
                _ = HandleConnectionAsync(server, requireToken, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, bool requireToken, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
            using var reader = new StreamReader(pipe, Utf8, false, 8192, leaveOpen: true);
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) return;

            var req = JsonNode.Parse(line)?.AsObject();
            var reqId = req?["id"]?.GetValue<string>() ?? "0";
            var token = req?["token"]?.GetValue<string>();
            var command = req?["command"]?.GetValue<string>() ?? "";
            var args = req?["args"] as JsonObject ?? new JsonObject();

            if (requireToken && token != _token)
            {
                await WriteResponseAsync(pipe, new JsonObject
                {
                    ["id"] = reqId,
                    ["ok"] = false,
                    ["error"] = new JsonObject
                    {
                        ["code"] = "unauthorized",
                        ["message"] = "Token MCP không khớp."
                    }
                }, ct);
                return;
            }

            JsonNode data;
            if (command == "__ping")
            {
                data = new JsonObject { ["pong"] = true, ["pid"] = _pid };
            }
            else if (command == "__catalog")
            {
                data = BuildCatalog();
            }
            else
            {
                data = await _window.ExecuteMcpCommandAsync(command, args, ct);
            }

            await WriteResponseAsync(pipe, new JsonObject
            {
                ["id"] = reqId,
                ["ok"] = true,
                ["data"] = data
            }, ct);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteResponseAsync(pipe, new JsonObject
                {
                    ["id"] = "err",
                    ["ok"] = false,
                    ["error"] = new JsonObject
                    {
                        ["code"] = "exception",
                        ["message"] = ex.Message
                    }
                }, ct);
            }
            catch
            {
                // Ignore broken pipe
            }
        }
        }
    }

    private static async Task WriteResponseAsync(Stream pipe, JsonObject resp, CancellationToken ct)
    {
        var bytes = Utf8.GetBytes(resp.ToJsonString(JsonOpts) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    private static JsonObject BuildCatalog()
    {
        return new JsonObject
        {
            ["product"] = "BIMUP CDE Studio",
            ["bridge_version"] = "1.0.0",
            ["toolsets"] = new JsonObject
            {
                ["explorer"] = "Đọc và điều khiển cây thư mục, dự án, danh sách file trên Autodesk Construction Cloud (ACC)",
                ["governance"] = "Đọc toàn bộ Roles, Thành viên (Members), Công ty, Phân quyền thư mục, Quy trình phê duyệt (Workflows), Đợt trình duyệt (Reviews), Transmittals và Issues"
            },
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "get_status",
                    ["toolset"] = "explorer",
                    ["description"] = "Lấy trạng thái hiện tại của BIMUP.CDE: Hub đang chọn, Dự án đang chọn, đường dẫn thư mục, số lượng folder/file/role/member/workflow/review.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "list_projects",
                    ["toolset"] = "explorer",
                    ["description"] = "Liệt kê tất cả các Dự án (Projects) trong ACC Hub hiện tại.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "select_project",
                    ["toolset"] = "explorer",
                    ["description"] = "Chọn một Dự án trên giao diện BIMUP.CDE theo tên hoặc ID và nạp cây thư mục + Roles + Members + Workflows + Reviews của dự án đó.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["project_name_or_id"] = new JsonObject { ["type"] = "string", ["description"] = "Tên hoặc ID của dự án cần mở" }
                        },
                        ["required"] = new JsonArray("project_name_or_id")
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_folders",
                    ["toolset"] = "explorer",
                    ["description"] = "Lấy danh sách các Thư mục (Folders) đang hiển thị trong BIMUP.CDE.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "open_folder",
                    ["toolset"] = "explorer",
                    ["description"] = "Mở vào một Thư mục con theo tên hoặc đường dẫn hoặc Folder ID và trả về danh sách thư mục con + file bên trong.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["folder_name_or_path"] = new JsonObject { ["type"] = "string", ["description"] = "Tên thư mục, đường dẫn hoặc ID thư mục cần mở" }
                        },
                        ["required"] = new JsonArray("folder_name_or_path")
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_files",
                    ["toolset"] = "explorer",
                    ["description"] = "Lấy danh sách các File (Items & Versions) đang hiển thị trong BIMUP.CDE.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "scan_full_project",
                    ["toolset"] = "explorer",
                    ["description"] = "Quét đệ quy toàn bộ cây thư mục và tất cả các file của Dự án đang chọn.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_roles",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy toàn bộ danh sách Role (Chức danh / Vai trò) của Dự án đang chọn.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_members",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy toàn bộ danh sách Thành viên dự án (Project Members), Thành viên toàn Hub (Account Users) và Công ty (Companies).",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_folder_permissions",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy bảng Phân quyền thư mục (Folder Permissions) cho các thư mục trong dự án.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_workflows",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy toàn bộ các Quy trình phê duyệt hồ sơ (Approval Workflows) của dự án đang chọn.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_reviews",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy toàn bộ các Đợt trình duyệt hồ sơ (Reviews) kèm danh sách file đính kèm và bước xử lý tiếp theo.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_issues",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy danh sách Issues (Vấn đề dự án) và Transmittals (Hồ sơ phát hành) của dự án.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "load_project_governance",
                    ["toolset"] = "governance",
                    ["description"] = "Tải và đồng bộ toàn bộ Roles, Thành viên (Members), Phân quyền thư mục (Permissions), Quy trình phê duyệt (Workflows), Đợt trình duyệt (Reviews), Transmittals và Issues của dự án đang chọn.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "get_logs",
                    ["toolset"] = "explorer",
                    ["description"] = "Đọc toàn bộ nội dung Nhật ký hoạt động của BIMUP.CDE.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                }
            }
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (File.Exists(_descriptorPath))
                File.Delete(_descriptorPath);
        }
        catch
        {
            // Ignore cleanup error
        }
    }
}
