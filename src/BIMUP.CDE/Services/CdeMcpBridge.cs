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
                ["product"] = "SGC CDE Studio",
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
                // Chỉ tài khoản Windows đang chạy app được mở pipe (tiến trình của user khác trên máy không gọi được).
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    System.Security.AccessControl.AccessControlType.Allow));

                var server = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0, 0,
                    security);

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

    /// <summary>Schema chung cho lệnh đọc có lọc/cắt trang; extra = tham số riêng của lệnh.</summary>
    private static JsonObject PagingSchema(JsonObject? extra = null)
    {
        var props = new JsonObject
        {
            ["name_contains"] = new JsonObject { ["type"] = "string", ["description"] = "Chỉ lấy mục có tên/tiêu đề/trạng thái chứa chuỗi này (không phân biệt hoa thường)" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Số mục tối đa trả về (mặc định 200, tối đa 1000)" },
            ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Bỏ qua N mục đầu (để lấy trang tiếp theo)" }
        };
        if (extra != null)
            foreach (var kv in extra.ToList()) { extra.Remove(kv.Key); props[kv.Key] = kv.Value; }
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject BuildCatalog()
    {
        return new JsonObject
        {
            ["product"] = "SGC CDE Studio",
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
                    ["description"] = "Lấy trạng thái hiện tại của SGC CDE: Hub đang chọn, Dự án đang chọn, đường dẫn thư mục, số lượng folder/file/role/member/workflow/review.",
                    ["read_only"] = true,
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                },
                new JsonObject
                {
                    ["name"] = "connect",
                    ["toolset"] = "explorer",
                    ["description"] = "Kết nối ACC bằng APS Client ID/Secret đã lưu trong cấu hình (như nút Kết nối), rồi trả trạng thái.",
                    ["read_only"] = false,
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
                    ["description"] = "Chọn một Dự án trên giao diện SGC CDE theo tên hoặc ID và nạp cây thư mục + Roles + Members + Workflows + Reviews của dự án đó.",
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
                    ["description"] = "Lấy danh sách các Thư mục (Folders) đang hiển thị trong SGC CDE.",
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
                    ["description"] = "Lấy danh sách các File (Items & Versions) đang hiển thị trong SGC CDE. Có name_contains/limit/offset.",
                    ["read_only"] = true,
                    ["input_schema"] = PagingSchema()
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
                    ["description"] = "Lấy các Quy trình phê duyệt hồ sơ (Approval Workflows) của dự án đang chọn. Mặc định không trả quy trình đã ẩn.",
                    ["read_only"] = true,
                    ["input_schema"] = PagingSchema(new JsonObject
                    {
                        ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = trả cả quy trình đang bị ẩn (mặc định false)" }
                    })
                },
                new JsonObject
                {
                    ["name"] = "hide_workflow",
                    ["toolset"] = "governance",
                    ["description"] = "Ẩn một Quy trình phê duyệt khỏi SGC CDE (lưu bền trong cde_settings.json). Chỉ ẩn trong app, không đổi gì trên ACC.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["workflow_id_or_name"] = new JsonObject { ["type"] = "string", ["description"] = "ID hoặc tên quy trình" }
                        },
                        ["required"] = new JsonArray("workflow_id_or_name")
                    }
                },
                new JsonObject
                {
                    ["name"] = "unhide_workflow",
                    ["toolset"] = "governance",
                    ["description"] = "Bỏ ẩn một Quy trình phê duyệt đã bị ẩn bằng hide_workflow.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["workflow_id_or_name"] = new JsonObject { ["type"] = "string", ["description"] = "ID hoặc tên quy trình" }
                        },
                        ["required"] = new JsonArray("workflow_id_or_name")
                    }
                },
                new JsonObject
                {
                    ["name"] = "set_hide_submit_workflows",
                    ["toolset"] = "governance",
                    ["description"] = "Bật/tắt việc ẩn nhóm quy trình Nộp mô hình BIM (5001/5101), chỉ giữ quy trình Phát hành. Mặc định bật.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["enabled"] = new JsonObject { ["type"] = "boolean" }
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_reviews",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy các Đợt trình duyệt hồ sơ (Reviews) kèm danh sách file đính kèm và bước xử lý tiếp theo. Có name_contains/limit/offset.",
                    ["read_only"] = true,
                    ["input_schema"] = PagingSchema()
                },
                new JsonObject
                {
                    ["name"] = "get_issues",
                    ["toolset"] = "governance",
                    ["description"] = "Lấy danh sách Issues (Vấn đề dự án) và Transmittals (Hồ sơ phát hành) của dự án. Có name_contains/limit/offset (áp cho cả hai danh sách).",
                    ["read_only"] = true,
                    ["input_schema"] = PagingSchema()
                },
                new JsonObject
                {
                    ["name"] = "export_workflows_json",
                    ["toolset"] = "governance",
                    ["description"] = "Xuất toàn bộ Quy trình phê duyệt của dự án ra file JSON (dùng làm bản sao lưu trước khi tạo quy trình mới). Mặc định ghi vào %LOCALAPPDATA%\\BIMUP\\exports.",
                    ["read_only"] = false,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Đường dẫn file .json (bỏ trống = tự đặt tên)" },
                            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "Gồm cả quy trình đang ẩn (mặc định true)" }
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = "create_workflow",
                    ["toolset"] = "governance",
                    ["description"] = "Tạo Quy trình phê duyệt mới trên ACC từ danh sách bước, hoặc nhân bản từ quy trình có sẵn (from_workflow). " +
                                      "MẶC ĐỊNH dry_run=true: chỉ trả payload sẽ gửi. ACC KHÔNG cho sửa/xóa workflow qua API nên chỉ đặt dry_run=false khi chắc chắn. Tên phải duy nhất.",
                    ["read_only"] = false,
                    ["destructive"] = true,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Tên quy trình mới (duy nhất)" },
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["from_workflow"] = new JsonObject { ["type"] = "string", ["description"] = "ID hoặc tên quy trình nguồn để nhân bản các bước" },
                            ["steps"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Các bước (bỏ trống nếu dùng from_workflow). Role gọi theo tên như trong danh sách Roles của dự án.",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["name"] = new JsonObject { ["type"] = "string" },
                                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("INITIATOR", "REVIEWER", "APPROVER") },
                                        ["duration"] = new JsonObject { ["type"] = "integer", ["description"] = "Số ngày xử lý (0 = không giới hạn)" },
                                        ["due_date_type"] = new JsonObject { ["type"] = "string", ["description"] = "Mặc định CALENDAR_DAY" },
                                        ["roles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                                    },
                                    ["required"] = new JsonArray("name", "type")
                                }
                            },
                            ["copy_enabled"] = new JsonObject { ["type"] = "boolean" },
                            ["copy_target_folder_urn"] = new JsonObject { ["type"] = "string" },
                            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Mặc định true (chỉ chạy thử)" },
                            ["confirm_project"] = new JsonObject { ["type"] = "string", ["description"] = "Bắt buộc khi dry_run=false: đúng tên dự án đang chọn (xem trường project trong kết quả dry_run)" }
                        },
                        ["required"] = new JsonArray("name")
                    }
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
                    ["description"] = "Đọc toàn bộ nội dung Nhật ký hoạt động của SGC CDE.",
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
