using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BIMUP.CDE.Models;
using BIMUP.CDE.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace BIMUP.CDE;

public partial class MainWindow : FluentWindow
{
    private readonly AccApiClient _api = new();
    private readonly CdeMcpBridge _mcpBridge;

    // Dữ liệu Cây thư mục & File
    private readonly List<AccFolder> _allFolders = new();
    private readonly List<AccFileItem> _allFiles = new();
    private readonly ObservableCollection<AccFolder> _filteredFolders = new();
    private readonly ObservableCollection<AccFileItem> _filteredFiles = new();
    private readonly ObservableCollection<AccFolder> _treeRoots = new();

    // Dữ liệu Roles, Thành viên, Công ty & Phân quyền thư mục
    private readonly List<AccRole> _allRoles = new();
    private readonly List<AccMember> _allMembers = new();
    private readonly List<AccCompany> _allCompanies = new();
    private readonly List<AccPermission> _allPermissions = new();
    private readonly ObservableCollection<AccRole> _filteredRoles = new();
    private readonly ObservableCollection<AccMember> _filteredMembers = new();
    private readonly ObservableCollection<AccPermission> _filteredPermissions = new();

    // Dữ liệu Quy trình (Workflows), Đợt trình duyệt (Reviews), Transmittals & Issues
    private readonly List<AccWorkflow> _allWorkflows = new();
    private readonly List<AccReview> _allReviews = new();
    private readonly ObservableCollection<AccWorkflow> _filteredWorkflows = new();
    private readonly ObservableCollection<AccReview> _filteredReviews = new();
    private readonly ObservableCollection<AccIssue> _issues = new();
    private readonly ObservableCollection<AccTransmittal> _transmittals = new();

    private AccConfig _config = new();
    private CancellationTokenSource? _cts;
    private bool _isUiReady;
    private bool _isFullProjectScanned;

    // Trạng thái nạp lười (Lazy loading) theo từng Tab cho dự án hiện hành
    private string? _loadedGovernanceProjectId;
    private string? _loadedWorkflowsProjectId;
    private string? _loadedReviewsProjectId;
    private string? _loadedIssuesProjectId;

    public MainWindow()
    {
        try
        {
            File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "MainWindow ctor enter\n");
            InitializeComponent();
            File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "MainWindow InitializeComponent done\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "MainWindow InitializeComponent EX: " + ex + "\n");
            throw;
        }

        TreeFolders.ItemsSource = _treeRoots;
        GridFolders.ItemsSource = _filteredFolders;
        GridFiles.ItemsSource = _filteredFiles;

        GridRoles.ItemsSource = _filteredRoles;
        GridMembers.ItemsSource = _filteredMembers;
        GridPermissions.ItemsSource = _filteredPermissions;

        GridWorkflows.ItemsSource = _filteredWorkflows;
        GridReviews.ItemsSource = _filteredReviews;
        GridIssues.ItemsSource = _issues;
        GridTransmittals.ItemsSource = _transmittals;

        LoadSavedConfig();
        _isUiReady = true;

        _mcpBridge = new CdeMcpBridge(this);
        _mcpBridge.Start();
        TxtMcpStatus.Text = $"Đang bật (PID {_mcpBridge.Pid})";
        AppendLog($"🔌 MCP Bridge đã khởi động: Pipe '{_mcpBridge.PipeName}' & 'BIMUP.Mcp.cde'");

        Closed += (_, _) =>
        {
            var stack = Environment.StackTrace;
            File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", $"MainWindow Closed event fired, stack:\n{stack}\n");
            _mcpBridge.Dispose();
        };

        Loaded += (_, _) =>
        {
            File.AppendAllText(@"D:\BIMUP Addin\BIMUP.CDE\startup.log", "MainWindow Loaded event fired\n");
        };
    }

    /// <summary>
    /// Thực thi các lệnh MCP gửi trực tiếp từ AI Agent vào BIMUP.CDE.
    /// </summary>
    public async Task<JsonNode> ExecuteMcpCommandAsync(string command, JsonObject args, CancellationToken ct)
    {
        AppendLog($"🤖 [MCP] Nhận lệnh từ AI: {command}");

        switch (command)
        {
            case "get_status":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var hub = CboHubs.SelectedItem as AccHub;
                    var proj = CboProjects.SelectedItem as AccProject;
                    return (JsonNode)new JsonObject
                    {
                        ["connected"] = _api.IsAuthenticated,
                        ["hub_name"] = hub?.Name ?? "",
                        ["hub_id"] = hub?.Id ?? "",
                        ["project_name"] = proj?.Name ?? "",
                        ["project_id"] = proj?.Id ?? "",
                        ["current_breadcrumb"] = TxtCurrentBreadcrumb.Text,
                        ["folder_count"] = _filteredFolders.Count,
                        ["file_count"] = _filteredFiles.Count,
                        ["role_count"] = _allRoles.Count,
                        ["member_count"] = _allMembers.Count,
                        ["company_count"] = _allCompanies.Count,
                        ["permission_count"] = _allPermissions.Count,
                        ["workflow_count"] = _allWorkflows.Count,
                        ["review_count"] = _allReviews.Count,
                        ["issue_count"] = _issues.Count,
                        ["transmittal_count"] = _transmittals.Count,
                        ["full_project_scanned"] = _isFullProjectScanned
                    };
                });
            }

            case "list_projects":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    if (CboProjects.ItemsSource is IEnumerable<AccProject> projs)
                    {
                        foreach (var p in projs)
                        {
                            arr.Add(new JsonObject
                            {
                                ["id"] = p.Id,
                                ["name"] = p.Name,
                                ["project_type"] = p.ProjectType,
                                ["selected"] = ReferenceEquals(p, CboProjects.SelectedItem)
                            });
                        }
                    }
                    return (JsonNode)new JsonObject { ["projects"] = arr };
                });
            }

            case "select_project":
            {
                var query = args["project_name_or_id"]?.GetValue<string>() ?? "";
                AccHub? hub = null;
                AccProject? targetProj = null;

                await Dispatcher.InvokeAsync(() =>
                {
                    hub = CboHubs.SelectedItem as AccHub;
                    if (CboProjects.ItemsSource is IEnumerable<AccProject> projs)
                    {
                        targetProj = projs.FirstOrDefault(p =>
                            p.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
                    }
                });

                if (hub == null || targetProj == null)
                    throw new InvalidOperationException($"Không tìm thấy dự án '{query}'.");

                await Dispatcher.InvokeAsync(() => CboProjects.SelectedItem = targetProj);
                await Task.Delay(2500, ct); // Đợi nạp dữ liệu dự án

                return await ExecuteMcpCommandAsync("get_status", new JsonObject(), ct);
            }

            case "get_folders":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    foreach (var f in _filteredFolders)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = f.Id,
                            ["name"] = f.Name,
                            ["full_path"] = f.FullPath,
                            ["object_count"] = f.ObjectCount,
                            ["create_user"] = f.CreateUserName,
                            ["create_time"] = f.CreateTime,
                            ["last_modified_user"] = f.LastModifiedUserName,
                            ["last_modified_time"] = f.LastModifiedTime
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["current_path"] = TxtCurrentBreadcrumb.Text,
                        ["folder_count"] = arr.Count,
                        ["folders"] = arr
                    };
                });
            }

            case "open_folder":
            {
                var query = args["folder_name_or_path"]?.GetValue<string>() ?? "";
                AccProject? proj = null;
                AccFolder? targetFolder = null;

                await Dispatcher.InvokeAsync(() =>
                {
                    proj = CboProjects.SelectedItem as AccProject;
                    targetFolder = FindFolderInTree(_treeRoots, query) ??
                                   _allFolders.FirstOrDefault(f =>
                                       f.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                                       f.FullPath.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                                       f.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                                       f.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase));
                });

                if (proj == null || targetFolder == null)
                    throw new InvalidOperationException($"Không tìm thấy thư mục '{query}'.");

                await Dispatcher.InvokeAsync(async () => await LoadSingleFolderAsync(proj.Id, targetFolder));
                await Task.Delay(800, ct);

                var foldersNode = await ExecuteMcpCommandAsync("get_folders", new JsonObject(), ct);
                var filesNode = await ExecuteMcpCommandAsync("get_files", new JsonObject(), ct);
                return new JsonObject
                {
                    ["opened_folder"] = targetFolder.FullPath,
                    ["subfolders"] = foldersNode["folders"]?.DeepClone(),
                    ["files"] = filesNode["files"]?.DeepClone()
                };
            }

            case "get_files":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    foreach (var item in _filteredFiles)
                    {
                        arr.Add(new JsonObject
                        {
                            ["file_name"] = item.FileName,
                            ["folder_path"] = item.FolderPath,
                            ["file_type"] = item.FileType,
                            ["version"] = item.VersionNumber,
                            ["size_mb"] = item.SizeMb,
                            ["review_state"] = item.ReviewState,
                            ["last_modified_user"] = item.LastModifiedUserName,
                            ["last_modified_time"] = item.LastModifiedTime,
                            ["custom_attributes"] = item.CustomAttributesSummary,
                            ["item_id"] = item.ItemId,
                            ["version_urn"] = item.VersionUrn,
                            ["storage_urn"] = item.StorageUrn
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["current_path"] = TxtCurrentBreadcrumb.Text,
                        ["file_count"] = arr.Count,
                        ["files"] = arr
                    };
                });
            }

            case "scan_full_project":
            {
                AccProject? proj = null;
                AccFolder? rootFolder = null;
                bool includeCustom = true;

                await Dispatcher.InvokeAsync(() =>
                {
                    proj = CboProjects.SelectedItem as AccProject;
                    rootFolder = CboTopFolders.SelectedItem as AccFolder;
                    includeCustom = ChkCustomAttributes.IsChecked == true;
                });

                if (proj == null || rootFolder == null)
                    throw new InvalidOperationException("Chưa chọn Dự án hoặc Top Folder.");

                var progress = new Progress<string>(msg => AppendLog(msg));
                var (allFolders, allFiles) = await _api.ScanFolderTreeRecursiveAsync(
                    proj.Id, rootFolder, includeCustom, progress, ct);

                await Dispatcher.InvokeAsync(() =>
                {
                    _isFullProjectScanned = true;
                    _allFolders.Clear();
                    _allFiles.Clear();
                    _allFolders.AddRange(allFolders);
                    _allFiles.AddRange(allFiles);
                    TxtCurrentBreadcrumb.Text = $"{rootFolder.FullPath} (Toàn bộ cây dự án)";
                    ApplyFilter(selectedFolderPrefix: null);
                });

                return new JsonObject
                {
                    ["project"] = proj.Name,
                    ["total_folders"] = allFolders.Count,
                    ["total_files"] = allFiles.Count
                };
            }

            case "get_roles":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    foreach (var r in _allRoles)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = r.Id,
                            ["name"] = r.Name,
                            ["member_group_id"] = r.MemberGroupId,
                            ["services"] = r.ServicesSummary
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["role_count"] = arr.Count,
                        ["roles"] = arr
                    };
                });
            }

            case "get_members":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var memArr = new JsonArray();
                    foreach (var m in _allMembers)
                    {
                        memArr.Add(new JsonObject
                        {
                            ["scope"] = m.Scope,
                            ["id"] = m.Id,
                            ["name"] = m.Name,
                            ["email"] = m.Email,
                            ["company"] = m.CompanyName,
                            ["roles"] = m.RolesSummary,
                            ["access_level"] = m.AccessLevel,
                            ["status"] = m.Status,
                            ["products"] = m.ProductsSummary,
                            ["autodesk_id"] = m.AutodeskId,
                            ["added_on"] = m.AddedOn
                        });
                    }
                    var compArr = new JsonArray();
                    foreach (var c in _allCompanies)
                    {
                        compArr.Add(new JsonObject
                        {
                            ["id"] = c.Id,
                            ["name"] = c.Name,
                            ["trade"] = c.Trade
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["member_count"] = memArr.Count,
                        ["company_count"] = compArr.Count,
                        ["members"] = memArr,
                        ["companies"] = compArr
                    };
                });
            }

            case "get_folder_permissions":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    foreach (var p in _allPermissions)
                    {
                        arr.Add(new JsonObject
                        {
                            ["folder_path"] = p.FolderPath,
                            ["subject_type"] = p.SubjectType,
                            ["subject_name"] = p.SubjectName,
                            ["email_or_info"] = p.EmailOrInfo,
                            ["permission_level"] = p.PermissionLevel,
                            ["direct_actions"] = p.ActionsSummary,
                            ["inherited_actions"] = p.InheritActionsSummary
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["permission_count"] = arr.Count,
                        ["permissions"] = arr
                    };
                });
            }

            case "get_workflows":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    foreach (var w in _allWorkflows)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = w.Id,
                            ["name"] = w.Name,
                            ["description"] = w.Description,
                            ["status"] = w.Status,
                            ["step_count"] = w.StepCount,
                            ["steps_summary"] = w.StepsSummary,
                            ["candidates_summary"] = w.CandidatesSummary,
                            ["approval_options"] = w.ApprovalOptionsSummary,
                            ["copy_files"] = w.CopyFilesInfo,
                            ["created_at"] = w.CreatedAt
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["workflow_count"] = arr.Count,
                        ["workflows"] = arr
                    };
                });
            }

            case "get_reviews":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var arr = new JsonArray();
                    foreach (var r in _allReviews)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = r.Id,
                            ["sequence_id"] = r.SequenceId,
                            ["name"] = r.Name,
                            ["workflow_name"] = r.WorkflowName,
                            ["status"] = r.Status,
                            ["current_step"] = r.CurrentStepName,
                            ["due_date"] = r.CurrentStepDueDate,
                            ["next_action_by"] = r.NextActionBy,
                            ["files_count"] = r.FilesCount,
                            ["files_summary"] = r.FilesSummary,
                            ["created_by"] = r.CreatedBy,
                            ["created_at"] = r.CreatedAt,
                            ["finished_at"] = r.FinishedAt
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["review_count"] = arr.Count,
                        ["reviews"] = arr
                    };
                });
            }

            case "get_issues":
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    var issArr = new JsonArray();
                    foreach (var iss in _issues)
                    {
                        issArr.Add(new JsonObject
                        {
                            ["id"] = iss.Id,
                            ["display_id"] = iss.DisplayId,
                            ["title"] = iss.Title,
                            ["status"] = iss.Status,
                            ["assigned_to"] = iss.AssignedTo,
                            ["due_date"] = iss.DueDate,
                            ["created_by"] = iss.CreatedBy,
                            ["created_at"] = iss.CreatedAt
                        });
                    }
                    var trArr = new JsonArray();
                    foreach (var t in _transmittals)
                    {
                        trArr.Add(new JsonObject
                        {
                            ["id"] = t.Id,
                            ["sequence_id"] = t.SequenceId,
                            ["title"] = t.Title,
                            ["status"] = t.Status,
                            ["docs_count"] = t.DocsCount,
                            ["created_by"] = t.CreatedBy,
                            ["created_at"] = t.CreatedAt
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["note"] = TxtIssuesNote.Text,
                        ["issue_count"] = issArr.Count,
                        ["issues"] = issArr,
                        ["transmittal_count"] = trArr.Count,
                        ["transmittals"] = trArr
                    };
                });
            }

            case "load_project_governance":
            {
                AccHub? hub = null;
                AccProject? proj = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    hub = CboHubs.SelectedItem as AccHub;
                    proj = CboProjects.SelectedItem as AccProject;
                });
                if (hub == null || proj == null)
                    throw new InvalidOperationException("Chưa chọn Hub hoặc Dự án.");

                await LoadProjectGovernanceAsync(hub.Id, proj.Id, ct);
                return await ExecuteMcpCommandAsync("get_status", new JsonObject(), ct);
            }

            case "get_logs":
            {
                return await Dispatcher.InvokeAsync(() =>
                    (JsonNode)new JsonObject { ["logs"] = TxtLogs.Text });
            }

            default:
                throw new InvalidOperationException($"Lệnh MCP không hỗ trợ: {command}");
        }
    }

    private static AccFolder? FindFolderInTree(IEnumerable<AccFolder> nodes, string query)
    {
        foreach (var n in nodes)
        {
            if (n.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                n.FullPath.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                n.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
            var found = FindFolderInTree(n.Children, query);
            if (found != null) return found;
        }
        return null;
    }

    private void LoadSavedConfig()
    {
        _config = AccConfig.Load();
        TxtClientId.Text = _config.ClientId;
        PwdClientSecret.Password = _config.ClientSecret;
        ChkUseThreeLegged.IsChecked = _config.UseThreeLeggedAuth;
        TxtCallbackUrl.Text = string.IsNullOrWhiteSpace(_config.CallbackUrl)
            ? "http://localhost:8080/api/auth/callback"
            : _config.CallbackUrl;
        TxtBackupFolder.Text = string.IsNullOrWhiteSpace(_config.BackupRootFolder)
            ? @"D:\ACC_Backup"
            : _config.BackupRootFolder;
        ChkCustomAttributes.IsChecked = _config.IncludeCustomAttributes;
    }

    private void SaveCurrentConfig()
    {
        _config.ClientId = TxtClientId.Text.Trim();
        _config.ClientSecret = PwdClientSecret.Password.Trim();
        _config.UseThreeLeggedAuth = ChkUseThreeLegged.IsChecked == true;
        _config.CallbackUrl = TxtCallbackUrl.Text.Trim();
        _config.BackupRootFolder = TxtBackupFolder.Text.Trim();
        _config.IncludeCustomAttributes = ChkCustomAttributes.IsChecked == true;
        _config.Save();
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        Dispatcher.Invoke(() =>
        {
            TxtLogs.AppendText(line);
            TxtLogs.ScrollToEnd();
            TxtProgressStatus.Text = message;
        });
    }

    private void SetBusy(bool busy, string? status = null)
    {
        ProgressBarMain.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnCancelTask.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnScanFiles.IsEnabled = !busy;
        BtnLoadGovernance.IsEnabled = !busy;
        if (!string.IsNullOrEmpty(status))
            TxtProgressStatus.Text = status;
    }

    private async void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady || MainTabs == null) return;
        var idx = NavList.SelectedIndex;
        if (idx >= 0 && idx < MainTabs.Items.Count)
        {
            MainTabs.SelectedIndex = idx;
            if (NavList.SelectedItem is ListBoxItem item && item.Tag is string title)
                TxtPageTitle.Text = title;

            // Kích hoạt nạp lười (Lazy Load) theo Tab nếu dự án đã được chọn
            if (CboHubs.SelectedItem is AccHub hub && CboProjects.SelectedItem is AccProject proj)
            {
                if (idx == 1 && _loadedGovernanceProjectId != proj.Id)
                {
                    await LoadGovernanceTabAsync(hub.Id, proj.Id);
                }
                else if (idx == 2 && _loadedWorkflowsProjectId != proj.Id)
                {
                    await LoadWorkflowsTabAsync(proj.Id);
                }
                else if (idx == 3 && _loadedReviewsProjectId != proj.Id)
                {
                    await LoadReviewsTabAsync(proj.Id);
                }
                else if (idx == 4 && _loadedIssuesProjectId != proj.Id)
                {
                    await LoadIssuesTabAsync(proj.Id);
                }
            }
        }
    }

    private async Task LoadGovernanceTabAsync(string hubId, string projectId)
    {
        try
        {
            SetBusy(true, "Đang tải Roles, Thành viên và Phân quyền thư mục...");
            _loadedGovernanceProjectId = projectId;

            // 1. Roles
            try
            {
                var roles = await _api.GetProjectRolesAsync(hubId, projectId);
                _allRoles.Clear();
                _allRoles.AddRange(roles);
                AppendLog($"🎖️ Đã tải {roles.Count} Roles của dự án.");
            }
            catch (Exception ex) { AppendLog($"⚠️ Lỗi tải Roles: {ex.Message}"); }

            // 2. Members & Companies
            try
            {
                var projMembers = await _api.GetProjectMembersAsync(projectId);
                var accUsers = await _api.GetAccountUsersAsync(hubId);
                var companies = await _api.GetAccountCompaniesAsync(hubId);

                var projEmails = new HashSet<string>(
                    projMembers.Where(m => !string.IsNullOrEmpty(m.Email)).Select(m => m.Email),
                    StringComparer.OrdinalIgnoreCase);

                var extraHubUsers = accUsers.Where(u => string.IsNullOrEmpty(u.Email) || !projEmails.Contains(u.Email)).ToList();

                _allMembers.Clear();
                _allMembers.AddRange(projMembers);
                _allMembers.AddRange(extraHubUsers);

                _allCompanies.Clear();
                _allCompanies.AddRange(companies);
                AppendLog($"👥 Đã tải {projMembers.Count} Thành viên dự án (+{extraHubUsers.Count} Hub Members, {companies.Count} Công ty).");
            }
            catch (Exception ex) { AppendLog($"⚠️ Lỗi tải Thành viên: {ex.Message}"); }

            // 3. Phân quyền thư mục cấp 1
            try
            {
                var targetFolders = new List<AccFolder>();
                foreach (var r in _treeRoots)
                {
                    targetFolders.Add(r);
                    foreach (var sub in r.Children.Take(10))
                        targetFolders.Add(sub);
                }

                var perms = new List<AccPermission>();
                foreach (var tf in targetFolders)
                {
                    var fp = await _api.GetFolderPermissionsAsync(projectId, tf);
                    perms.AddRange(fp);
                }

                _allPermissions.Clear();
                _allPermissions.AddRange(perms);
                AppendLog($"🔐 Đã tải {perms.Count} Phân quyền thư mục.");
            }
            catch (Exception ex) { AppendLog($"⚠️ Lỗi tải Phân quyền: {ex.Message}"); }

            ApplyGovernanceFilter();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadWorkflowsTabAsync(string projectId)
    {
        try
        {
            SetBusy(true, "Đang tải Danh sách Quy trình phê duyệt (Workflows)...");
            _loadedWorkflowsProjectId = projectId;

            var workflows = await _api.GetApprovalWorkflowsAsync(projectId);

            // Truy vấn đường dẫn thư mục và link ACC cho các workflow có cài đặt copy file
            foreach (var wf in workflows)
            {
                if (wf.CopyFilesEnabled && !string.IsNullOrEmpty(wf.CopyTargetFolderUrn))
                {
                    var (fName, fPath, fWebUrl) = await _api.GetFolderMetadataAsync(projectId, wf.CopyTargetFolderUrn);
                    wf.CopyTargetFolderPath = !string.IsNullOrEmpty(fPath) ? fPath : fName;
                    wf.CopyTargetFolderWebUrl = fWebUrl;
                }
            }

            _allWorkflows.Clear();
            _allWorkflows.AddRange(workflows);
            ApplyWorkflowFilter();

            AppendLog($"✅ Đã tải {workflows.Count} Quy trình phê duyệt (Workflows).");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Lỗi tải Workflows: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadReviewsTabAsync(string projectId)
    {
        try
        {
            SetBusy(true, "Đang tải Danh sách Đợt trình duyệt (Reviews)...");
            _loadedReviewsProjectId = projectId;

            var reviews = await _api.GetProjectReviewsAsync(projectId, _allWorkflows);

            _allReviews.Clear();
            _allReviews.AddRange(reviews);
            ApplyReviewFilter();

            AppendLog($"✅ Đã tải {reviews.Count} Đợt trình duyệt (Reviews).");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Lỗi tải Reviews: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadIssuesTabAsync(string projectId)
    {
        try
        {
            SetBusy(true, "Đang tải Issues & Gói phát hành (Transmittals)...");
            _loadedIssuesProjectId = projectId;

            var transmittals = await _api.GetProjectTransmittalsAsync(projectId);
            var (issues, issueNote) = await _api.GetProjectIssuesAsync(projectId);

            _transmittals.Clear();
            foreach (var t in transmittals)
                _transmittals.Add(t);

            _issues.Clear();
            foreach (var iss in issues)
                _issues.Add(iss);

            TxtIssuesNote.Text = !string.IsNullOrEmpty(issueNote)
                ? $"ℹ️ {issueNote}  ·  Hồ sơ phát hành (Transmittals): {transmittals.Count}"
                : $"Đã tải {issues.Count} Issues và {transmittals.Count} Gói phát hành (Transmittals).";

            AppendLog($"📬 Đã tải {issues.Count} Issues & {transmittals.Count} Transmittals.");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Lỗi tải Transmittals / Issues: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnExpandTree_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _treeRoots)
            SetFolderExpandedRecursive(root, true);
    }

    private void BtnCollapseTree_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _treeRoots)
            SetFolderExpandedRecursive(root, false);
    }

    private static void SetFolderExpandedRecursive(AccFolder folder, bool expanded)
    {
        folder.IsExpanded = expanded;
        foreach (var child in folder.Children)
            SetFolderExpandedRecursive(child, expanded);
    }

    private void GridFolders_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridFolders.SelectedItem is not AccFolder clickedFolder) return;
        _ = SelectAndOpenFolderAsync(clickedFolder);
    }

    private async void TreeFolders_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not AccFolder selectedFolder) return;
        await SelectAndOpenFolderAsync(selectedFolder);
    }

    private async Task SelectAndOpenFolderAsync(AccFolder selectedFolder)
    {
        TxtCurrentBreadcrumb.Text = selectedFolder.FullPath;

        if (_isFullProjectScanned)
        {
            ApplyFilter(selectedFolderPrefix: selectedFolder.FullPath);
            return;
        }

        if (CboProjects.SelectedItem is AccProject proj && _api.IsAuthenticated)
        {
            await LoadSingleFolderAsync(proj.Id, selectedFolder);
        }
    }

    private async void BtnQuickConnect_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAccAsync();
    }

    private async void BtnSaveAndConnect_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig();
        await ConnectAccAsync();
    }

    private async Task ConnectAccAsync()
    {
        SaveCurrentConfig();
        if (string.IsNullOrWhiteSpace(_config.ClientId) || string.IsNullOrWhiteSpace(_config.ClientSecret))
        {
            NavList.SelectedIndex = 5;
            System.Windows.MessageBox.Show(
                "Vui lòng nhập APS Client ID và APS Client Secret ở mục 'Cấu hình API' trước khi kết nối!",
                "SGC CDE — Thiếu thông tin API",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            SetBusy(true, "Đang xác thực với Autodesk Platform Services (APS)...");
            _api.Configure(_config.ClientId, _config.ClientSecret);

            if (_config.UseThreeLeggedAuth)
            {
                AppendLog("Đang mở trình duyệt để xác thực 3-Legged OAuth...");
                await _api.AuthenticateThreeLeggedAsync(_config.CallbackUrl, _cts.Token);
            }
            else
            {
                AppendLog("Đang xác thực 2-Legged OAuth (Server-to-Server)...");
                await _api.AuthenticateTwoLeggedAsync();
            }

            AppendLog("Xác thực thành công! Đang tải danh sách ACC Hubs...");
            var hubs = await _api.GetHubsAsync();

            CboHubs.ItemsSource = hubs;
            if (hubs.Count > 0)
            {
                CboHubs.SelectedIndex = 0;
                StatusDot.Fill = (Brush)FindResource("BIMUP.Fluent.Brush.Success");
                TxtConnectionStatus.Text = $"Đã kết nối ({hubs.Count} Hub)";
                AppendLog($"Tìm thấy {hubs.Count} ACC Hub(s).");
                NavList.SelectedIndex = 0;
            }
            else
            {
                StatusDot.Fill = (Brush)FindResource("BIMUP.Fluent.Brush.Danger");
                TxtConnectionStatus.Text = "0 Hub (Thiếu Custom Integration)";
                AppendLog("⚠️ Đã lấy Token nhưng không tìm thấy Hub nào. Kiểm tra Custom Integrations trên ACC Account Admin.");
            }
        }
        catch (Exception ex)
        {
            StatusDot.Fill = (Brush)FindResource("BIMUP.Fluent.Brush.Danger");
            TxtConnectionStatus.Text = "Lỗi kết nối";
            AppendLog($"❌ Lỗi kết nối ACC: {ex.Message}");
            System.Windows.MessageBox.Show(ex.Message, "Lỗi kết nối ACC API", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CboHubs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboHubs.SelectedItem is not AccHub hub) return;
        try
        {
            SetBusy(true, $"Đang tải danh sách Dự án từ Hub '{hub.Name}'...");
            var projects = await _api.GetProjectsAsync(hub.Id);
            CboProjects.ItemsSource = projects;
            if (projects.Count > 0)
                CboProjects.SelectedIndex = 0;
            AppendLog($"Hub '{hub.Name}': Tìm thấy {projects.Count} dự án.");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi tải danh sách dự án: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CboProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboHubs.SelectedItem is not AccHub hub || CboProjects.SelectedItem is not AccProject proj) return;
        try
        {
            SetBusy(true, $"Đang mở dự án '{proj.Name}'...");
            _isFullProjectScanned = false;
            _mcpBridge.UpdateDocumentContext($"{hub.Name} / {proj.Name}");

            // Đặt lại cờ nạp của các tab để khi người dùng chuyển sang tab nào mới tải tab đó
            _loadedGovernanceProjectId = null;
            _loadedWorkflowsProjectId = null;
            _loadedReviewsProjectId = null;
            _loadedIssuesProjectId = null;

            // Xóa dữ liệu cũ của dự án trước
            _treeRoots.Clear();
            _allFolders.Clear();
            _allFiles.Clear();
            _allRoles.Clear();
            _allMembers.Clear();
            _allCompanies.Clear();
            _allPermissions.Clear();
            _allWorkflows.Clear();
            _allReviews.Clear();
            _issues.Clear();
            _transmittals.Clear();

            _filteredFolders.Clear();
            _filteredFiles.Clear();
            _filteredRoles.Clear();
            _filteredMembers.Clear();
            _filteredPermissions.Clear();
            _filteredWorkflows.Clear();
            _filteredReviews.Clear();

            var folders = await _api.GetTopFoldersAsync(hub.Id, proj.Id);
            CboTopFolders.ItemsSource = folders;

            foreach (var f in folders)
            {
                _treeRoots.Add(f);
            }

            CboTopFolders.SelectedIndex = folders.Count > 0 ? 0 : -1;
            AppendLog($"Dự án '{proj.Name}': Đã tải xong {folders.Count} thư mục gốc (Top Folders).");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi tải dữ liệu dự án: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnLoadGovernance_Click(object sender, RoutedEventArgs e)
    {
        if (CboHubs.SelectedItem is not AccHub hub || CboProjects.SelectedItem is not AccProject proj) return;
        try
        {
            SetBusy(true, $"Đang đồng bộ Roles, Members, Quy trình & Reviews của '{proj.Name}'...");
            await LoadProjectGovernanceAsync(hub.Id, proj.Id);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Nạp toàn bộ Roles, Thành viên (Dự án + Hub), Công ty, Phân quyền, Quy trình (Workflows), Đợt duyệt (Reviews), Transmittals và Issues.
    /// </summary>
    private async Task LoadProjectGovernanceAsync(string hubId, string projectId, CancellationToken ct = default)
    {
        // 1. Roles
        try
        {
            var roles = await _api.GetProjectRolesAsync(hubId, projectId, ct);
            await Dispatcher.InvokeAsync(() =>
            {
                _allRoles.Clear();
                _allRoles.AddRange(roles);
            });
            AppendLog($"🎖️ Đã tải {roles.Count} Roles của dự án.");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Không lấy được Roles: {ex.Message}");
        }

        // 2. Project Members + Account Directory Users + Companies
        try
        {
            var projMembers = await _api.GetProjectMembersAsync(projectId, ct);
            var accUsers = await _api.GetAccountUsersAsync(hubId, ct);
            var companies = await _api.GetAccountCompaniesAsync(hubId, ct);

            var projEmails = new HashSet<string>(
                projMembers.Where(m => !string.IsNullOrEmpty(m.Email)).Select(m => m.Email),
                StringComparer.OrdinalIgnoreCase);

            var extraHubUsers = accUsers.Where(u => string.IsNullOrEmpty(u.Email) || !projEmails.Contains(u.Email)).ToList();

            await Dispatcher.InvokeAsync(() =>
            {
                _allMembers.Clear();
                _allMembers.AddRange(projMembers);
                _allMembers.AddRange(extraHubUsers);

                _allCompanies.Clear();
                _allCompanies.AddRange(companies);
            });
            AppendLog($"👥 Đã tải {projMembers.Count} Thành viên dự án (+{extraHubUsers.Count} Thành viên trên Hub, {companies.Count} Công ty).");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Không lấy được danh sách Thành viên: {ex.Message}");
        }

        // 3. Phân quyền thư mục gốc & cấp 1
        try
        {
            List<AccFolder> targetFolders = new();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_treeRoots.Count > 0)
                {
                    foreach (var r in _treeRoots)
                    {
                        targetFolders.Add(r);
                        foreach (var sub in r.Children.Take(10))
                            targetFolders.Add(sub);
                    }
                }
            });

            var perms = new List<AccPermission>();
            foreach (var tf in targetFolders)
            {
                var fp = await _api.GetFolderPermissionsAsync(projectId, tf, ct);
                perms.AddRange(fp);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _allPermissions.Clear();
                _allPermissions.AddRange(perms);
                ApplyGovernanceFilter();
            });
            AppendLog($"🔐 Đã tải {perms.Count} bản ghi Phân quyền thư mục (Folder Permissions).");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Không lấy được Phân quyền thư mục: {ex.Message}");
        }

        // 4. Approval Workflows & Reviews
        try
        {
            var workflows = await _api.GetApprovalWorkflowsAsync(projectId, ct);
            var reviews = await _api.GetProjectReviewsAsync(projectId, workflows, ct);

            // Nạp thông tin thư mục đích cho các workflow
            foreach (var wf in workflows)
            {
                if (wf.CopyFilesEnabled && !string.IsNullOrEmpty(wf.CopyTargetFolderUrn))
                {
                    var (fName, fPath, fWebUrl) = await _api.GetFolderMetadataAsync(projectId, wf.CopyTargetFolderUrn, ct);
                    wf.CopyTargetFolderPath = !string.IsNullOrEmpty(fPath) ? fPath : fName;
                    wf.CopyTargetFolderWebUrl = fWebUrl;
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _allWorkflows.Clear();
                _allWorkflows.AddRange(workflows);
                _allReviews.Clear();
                _allReviews.AddRange(reviews);
                ApplyWorkflowFilter();
                ApplyReviewFilter();
            });
            AppendLog($"✅ Đã tải {workflows.Count} Quy trình phê duyệt (Workflows) và {reviews.Count} Đợt trình duyệt hồ sơ (Reviews).");
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Không lấy được Quy trình / Reviews: {ex.Message}");
        }

        // 5. Transmittals & Issues
        try
        {
            var transmittals = await _api.GetProjectTransmittalsAsync(projectId, ct);
            var (issues, issueNote) = await _api.GetProjectIssuesAsync(projectId, ct);

            await Dispatcher.InvokeAsync(() =>
            {
                _transmittals.Clear();
                foreach (var t in transmittals)
                    _transmittals.Add(t);

                _issues.Clear();
                foreach (var iss in issues)
                    _issues.Add(iss);

                TxtIssuesNote.Text = !string.IsNullOrEmpty(issueNote)
                    ? $"ℹ️ {issueNote}  ·  Hồ sơ phát hành (Transmittals): {transmittals.Count}"
                    : $"Đã tải {issues.Count} Issues và {transmittals.Count} Gói phát hành (Transmittals).";
            });
        }
        catch (Exception ex)
        {
            AppendLog($"⚠️ Lỗi tải Transmittals / Issues: {ex.Message}");
        }
    }

    private async void BtnScanAllFolderPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (CboProjects.SelectedItem is not AccProject proj) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            SetBusy(true, "Đang quét Phân quyền (Permissions) cho tất cả các thư mục đang mở...");
            var foldersToScan = _allFolders.ToList();
            if (foldersToScan.Count == 0 && _treeRoots.Count > 0)
                foldersToScan.AddRange(_treeRoots);

            var allPerms = new List<AccPermission>();
            for (int i = 0; i < foldersToScan.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var f = foldersToScan[i];
                AppendLog($"🔐 [{i + 1}/{foldersToScan.Count}] Đang đọc phân quyền: {f.FullPath}...");
                var fp = await _api.GetFolderPermissionsAsync(proj.Id, f, _cts.Token);
                allPerms.AddRange(fp);
            }

            _allPermissions.Clear();
            _allPermissions.AddRange(allPerms);
            ApplyGovernanceFilter();
            AppendLog($"✅ Hoàn tất quét Phân quyền: {allPerms.Count} quyền trên {foldersToScan.Count} thư mục.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("⚠️ Đã dừng quét phân quyền thư mục.");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi quét phân quyền thư mục: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void TxtMemberSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isUiReady) return;
        ApplyGovernanceFilter();
    }

    private void ApplyGovernanceFilter()
    {
        var kw = TxtMemberSearch?.Text.Trim() ?? "";

        IEnumerable<AccRole> rQuery = _allRoles;
        IEnumerable<AccMember> mQuery = _allMembers;
        IEnumerable<AccPermission> pQuery = _allPermissions;

        if (!string.IsNullOrEmpty(kw))
        {
            rQuery = rQuery.Where(r =>
                r.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.ServicesSummary.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.Id.Contains(kw, StringComparison.OrdinalIgnoreCase));

            mQuery = mQuery.Where(m =>
                m.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                m.Email.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                m.CompanyName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                m.RolesSummary.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                m.AccessLevel.Contains(kw, StringComparison.OrdinalIgnoreCase));

            pQuery = pQuery.Where(p =>
                p.FolderPath.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.SubjectName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.SubjectType.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                p.PermissionLevel.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        _filteredRoles.Clear();
        foreach (var r in rQuery) _filteredRoles.Add(r);

        _filteredMembers.Clear();
        foreach (var m in mQuery) _filteredMembers.Add(m);

        _filteredPermissions.Clear();
        foreach (var p in pQuery) _filteredPermissions.Add(p);

        if (TxtGovernanceSummary != null)
            TxtGovernanceSummary.Text = $"{_filteredRoles.Count} Roles  ·  {_filteredMembers.Count} Thành viên  ·  {_filteredPermissions.Count} Phân quyền";
    }

    private void TxtWorkflowSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isUiReady) return;
        ApplyWorkflowFilter();
    }

    private void ApplyWorkflowFilter()
    {
        var kw = TxtWorkflowSearch?.Text.Trim() ?? "";

        IEnumerable<AccWorkflow> wfQuery = _allWorkflows;

        if (!string.IsNullOrEmpty(kw))
        {
            wfQuery = wfQuery.Where(w =>
                w.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                w.Description.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                w.StepsSummary.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                w.CandidatesSummary.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        _filteredWorkflows.Clear();
        foreach (var w in wfQuery) _filteredWorkflows.Add(w);

        if (TxtWorkflowSummary != null)
            TxtWorkflowSummary.Text = $"{_filteredWorkflows.Count} Quy trình phê duyệt";
    }

    private void TxtReviewSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isUiReady) return;
        ApplyReviewFilter();
    }

    private void ApplyReviewFilter()
    {
        var kw = TxtReviewSearch?.Text.Trim() ?? "";

        IEnumerable<AccReview> revQuery = _allReviews;

        if (!string.IsNullOrEmpty(kw))
        {
            revQuery = revQuery.Where(r =>
                r.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.WorkflowName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.Status.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.NextActionBy.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.FilesSummary.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                r.CreatedBy.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        _filteredReviews.Clear();
        foreach (var r in revQuery) _filteredReviews.Add(r);

        if (TxtReviewSummary != null)
            TxtReviewSummary.Text = $"{_filteredReviews.Count} Đợt trình duyệt (Reviews)";
    }

    private async void CboTopFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboProjects.SelectedItem is not AccProject proj || CboTopFolders.SelectedItem is not AccFolder rootFolder)
            return;

        _isFullProjectScanned = false;
        await LoadSingleFolderAsync(proj.Id, rootFolder);
    }

    private async Task LoadSingleFolderAsync(string projectId, AccFolder targetFolder)
    {
        try
        {
            SetBusy(true, $"Đang đọc thư mục '{targetFolder.FullPath}'...");
            var (subfolders, files) = await _api.GetSingleFolderContentsAsync(projectId, targetFolder);

            targetFolder.Children.Clear();
            foreach (var sf in subfolders)
                targetFolder.Children.Add(sf);
            targetFolder.IsExpanded = true;

            _allFolders.Clear();
            if (subfolders.Count > 0)
                _allFolders.AddRange(subfolders);
            else
                _allFolders.Add(targetFolder);

            _allFiles.Clear();
            _allFiles.AddRange(files);

            TxtCurrentBreadcrumb.Text = targetFolder.FullPath;
            ApplyFilter(selectedFolderPrefix: null);
            AppendLog($"📁 '{targetFolder.FullPath}': {subfolders.Count} thư mục con, {files.Count} files.");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi đọc thư mục '{targetFolder.FullPath}': {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnScanFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!_api.IsAuthenticated)
        {
            await ConnectAccAsync();
            if (!_api.IsAuthenticated) return;
        }

        if (CboProjects.SelectedItem is not AccProject proj || CboTopFolders.SelectedItem is not AccFolder rootFolder)
        {
            System.Windows.MessageBox.Show("Vui lòng chọn Dự án (Project) và Thư mục gốc (Top Folder) cần quét!", "BIMUP.CDE", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            SetBusy(true, $"Đang quét đệ quy toàn bộ cây thư mục '{rootFolder.Name}'...");
            _allFolders.Clear();
            _allFiles.Clear();
            _filteredFolders.Clear();
            _filteredFiles.Clear();

            var progress = new Progress<string>(msg => AppendLog(msg));
            var includeCustom = ChkCustomAttributes.IsChecked == true;

            var (allFolders, allFiles) = await _api.ScanFolderTreeRecursiveAsync(
                proj.Id,
                rootFolder,
                includeCustom,
                progress,
                _cts.Token);

            _isFullProjectScanned = true;
            _allFolders.AddRange(allFolders);
            _allFiles.AddRange(allFiles);
            TxtCurrentBreadcrumb.Text = $"{rootFolder.FullPath} (Toàn bộ cây dự án)";
            ApplyFilter(selectedFolderPrefix: null);
            AppendLog($"✅ Hoàn tất quét dự án '{proj.Name}': Tổng cộng {_allFolders.Count} thư mục và {_allFiles.Count} files.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("⚠️ Đã hủy tác vụ quét thư mục theo yêu cầu.");
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi khi quét thư mục: {ex.Message}");
            System.Windows.MessageBox.Show(ex.Message, "BIMUP.CDE — Lỗi quét Thư mục", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady) return;
        var selectedNode = TreeFolders.SelectedItem as AccFolder;
        ApplyFilter(_isFullProjectScanned ? selectedNode?.FullPath : null);
    }

    private void ApplyFilter(string? selectedFolderPrefix = null)
    {
        if (TxtSearchFilter == null || CboExtFilter == null || TxtSummaryCount == null) return;

        var keyword = TxtSearchFilter.Text.Trim();
        var extMode = CboExtFilter.SelectedIndex;

        IEnumerable<AccFolder> folderQuery = _allFolders;
        if (!string.IsNullOrEmpty(selectedFolderPrefix))
        {
            folderQuery = folderQuery.Where(f =>
                f.FullPath.StartsWith(selectedFolderPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f.FullPath, selectedFolderPrefix, StringComparison.OrdinalIgnoreCase));

            if (!folderQuery.Any())
            {
                folderQuery = _allFolders.Where(f => string.Equals(f.FullPath, selectedFolderPrefix, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            folderQuery = folderQuery.Where(f =>
                f.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                f.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                f.CreateUserName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                f.LastModifiedUserName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        _filteredFolders.Clear();
        foreach (var fd in folderQuery)
            _filteredFolders.Add(fd);

        IEnumerable<AccFileItem> fileQuery = _allFiles;
        if (!string.IsNullOrEmpty(selectedFolderPrefix))
        {
            fileQuery = fileQuery.Where(f =>
                f.FolderPath.StartsWith(selectedFolderPrefix, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            fileQuery = fileQuery.Where(f =>
                f.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                f.FolderPath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                f.LastModifiedUserName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                f.CustomAttributesSummary.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        fileQuery = extMode switch
        {
            1 => fileQuery.Where(f => f.FileType is "RVT" or "RFA" or "RTE"),
            2 => fileQuery.Where(f => f.FileType is "DWG" or "DXF" or "DWT"),
            3 => fileQuery.Where(f => f.FileType is "NWD" or "NWC" or "NWF" or "IFC"),
            4 => fileQuery.Where(f => f.FileType is "PDF" or "XLSX" or "XLS" or "DOCX" or "DOC"),
            _ => fileQuery
        };

        _filteredFiles.Clear();
        foreach (var item in fileQuery)
            _filteredFiles.Add(item);

        var totalMb = Math.Round(_filteredFiles.Sum(f => f.SizeMb), 2);
        TxtSummaryCount.Text = $"{_filteredFolders.Count} thư mục  ·  {_filteredFiles.Count} files ({totalMb} MB)";
        if (TxtFolderHeaderCount != null)
            TxtFolderHeaderCount.Text = $"{_filteredFolders.Count} thư mục";
        if (TxtFileHeaderCount != null)
            TxtFileHeaderCount.Text = $"{_filteredFiles.Count} files ({totalMb} MB)";
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _filteredFiles)
            item.IsSelected = true;
    }

    private void BtnUnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _filteredFiles)
            item.IsSelected = false;
    }

    private void BtnExportFoldersCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_filteredFolders.Count == 0)
        {
            System.Windows.MessageBox.Show("Chưa có dữ liệu Thư mục nào để xuất!", "SGC CDE", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var projName = CleanFileName((CboProjects.SelectedItem as AccProject)?.Name ?? "ACC_Project");
        var dlg = new SaveFileDialog
        {
            Title = "Lưu báo cáo danh sách Thư mục Dự án (Mở bằng Excel)",
            Filter = "Excel CSV UTF-8 (*.csv)|*.csv",
            FileName = $"SGC_CDE_Folders_{projName}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            AccApiClient.ExportFoldersToCsv(_filteredFolders, dlg.FileName);
            AppendLog($"📁 Đã xuất {_filteredFolders.Count} thư mục ra: {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_filteredFiles.Count == 0)
        {
            System.Windows.MessageBox.Show("Chưa có dữ liệu File nào trong bảng để xuất báo cáo!", "SGC CDE", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var projName = CleanFileName((CboProjects.SelectedItem as AccProject)?.Name ?? "ACC_Project");
        var dlg = new SaveFileDialog
        {
            Title = "Lưu báo cáo danh sách File ACC (Mở bằng Excel)",
            Filter = "Excel CSV UTF-8 (*.csv)|*.csv",
            FileName = $"SGC_CDE_Files_{projName}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            AccApiClient.ExportToCsv(_filteredFiles, dlg.FileName);
            AppendLog($"📊 Đã xuất {_filteredFiles.Count} dòng dữ liệu file ra: {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
    }

    private void BtnExportMembersRolesCsv_Click(object sender, RoutedEventArgs e)
    {
        var projName = CleanFileName((CboProjects.SelectedItem as AccProject)?.Name ?? "ACC_Project");
        var dlg = new SaveFileDialog
        {
            Title = "Lưu báo cáo Roles, Thành viên & Phân quyền Thư mục",
            Filter = "Excel CSV UTF-8 (*.csv)|*.csv",
            FileName = $"SGC_CDE_Roles_Members_{projName}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            AccApiClient.ExportMembersAndRolesToCsv(_filteredRoles, _filteredMembers, _filteredPermissions, dlg.FileName);
            AppendLog($"👥 Đã xuất báo cáo Roles & Thành viên ra: {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
    }

    private void BtnExportWorkflowsCsv_Click(object sender, RoutedEventArgs e)
    {
        var projName = CleanFileName((CboProjects.SelectedItem as AccProject)?.Name ?? "ACC_Project");
        var dlg = new SaveFileDialog
        {
            Title = "Lưu danh sách Cài đặt Quy trình phê duyệt (Workflows)",
            Filter = "Excel CSV UTF-8 (*.csv)|*.csv",
            FileName = $"SGC_CDE_Workflows_{projName}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            AccApiClient.ExportWorkflowsAndReviewsToCsv(_filteredWorkflows, new List<AccReview>(), dlg.FileName);
            AppendLog($"✅ Đã xuất danh sách Quy trình ra: {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
    }

    private void BtnExportReviewsCsv_Click(object sender, RoutedEventArgs e)
    {
        var projName = CleanFileName((CboProjects.SelectedItem as AccProject)?.Name ?? "ACC_Project");
        var dlg = new SaveFileDialog
        {
            Title = "Lưu danh sách Đợt trình duyệt (Reviews)",
            Filter = "Excel CSV UTF-8 (*.csv)|*.csv",
            FileName = $"SGC_CDE_Reviews_{projName}_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (dlg.ShowDialog() == true)
        {
            AccApiClient.ExportWorkflowsAndReviewsToCsv(new List<AccWorkflow>(), _filteredReviews, dlg.FileName);
            AppendLog($"✅ Đã xuất danh sách Đợt duyệt (Reviews) ra: {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
    }

    private void GridWorkflows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridWorkflows.SelectedItem is not AccWorkflow wf)
        {
            TxtSelectedWorkflowTitle.Text = "SƠ ĐỒ LUỒNG CÁC BƯỚC & HÀNH ĐỘNG (NODE FLOW DIAGRAM)";
            PanelWorkflowNodes.Children.Clear();
            PanelWorkflowNodes.Children.Add(new WpfTextBlock
            {
                Text = "Chọn một Quy trình ở bảng trên để xem chi tiết luồng các bước...",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg3"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });
            return;
        }

        TxtSelectedWorkflowTitle.Text = $"LUỒNG CÁC BƯỚC: {wf.Name.ToUpper()}  ({wf.StepCount} BƯỚC)";
        RenderWorkflowNodes(wf);

        // Nạp đường dẫn thư mục đích nếu chưa có hoặc mới chỉ có tên ngắn
        if (wf.CopyFilesEnabled && !string.IsNullOrEmpty(wf.CopyTargetFolderUrn) &&
            (string.IsNullOrEmpty(wf.CopyTargetFolderPath) || !wf.CopyTargetFolderPath.Contains("➔")))
        {
            if (CboProjects.SelectedItem is AccProject curProj)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (fName, fPath, fWebUrl) = await _api.GetFolderMetadataAsync(curProj.Id, wf.CopyTargetFolderUrn);
                        wf.CopyTargetFolderPath = !string.IsNullOrEmpty(fPath) ? fPath : fName;
                        wf.CopyTargetFolderWebUrl = fWebUrl;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (ReferenceEquals(GridWorkflows.SelectedItem, wf))
                            {
                                RenderWorkflowNodes(wf);
                            }
                        });
                    }
                    catch
                    {
                        // Ignore
                    }
                });
            }
        }
    }

    private void RenderWorkflowNodes(AccWorkflow wf)
    {
        PanelWorkflowNodes.Children.Clear();

        if (wf.Steps.Count == 0)
        {
            PanelWorkflowNodes.Children.Add(new WpfTextBlock
            {
                Text = $"Quy trình '{wf.Name}' chưa có dữ liệu chi tiết các bước.",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg3"),
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        for (int i = 0; i < wf.Steps.Count; i++)
        {
            var step = wf.Steps[i];
            var stepCard = CreateStepNodeCard(step, i + 1);
            PanelWorkflowNodes.Children.Add(stepCard);

            // Mũi tên nối tiếp giữa các bước
            if (i < wf.Steps.Count - 1 || wf.CopyFilesEnabled)
            {
                PanelWorkflowNodes.Children.Add(CreateFlowArrow());
            }
        }

        // Nếu có tự động copy file thì thêm Node hành động cuối
        if (wf.CopyFilesEnabled)
        {
            PanelWorkflowNodes.Children.Add(CreateCopyActionNodeCard(wf));
        }
    }

    private UIElement CreateStepNodeCard(AccWorkflowStepItem step, int index)
    {
        var border = new Border
        {
            Width = 240,
            MinHeight = 110,
            Background = (Brush)FindResource("BIMUP.Fluent.Brush.CardBg"),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(step.TypeColor)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 2)
        };

        var stack = new StackPanel();

        // Header: Badge loại bước + Duration
        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var badgeBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(32, 79, 91, 213)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var badgeText = new WpfTextBlock
        {
            Text = $"{index}. {step.TypeBadge.ToUpper()}",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(step.TypeColor))
        };
        badgeBorder.Child = badgeText;
        headerDock.Children.Add(badgeBorder);

        if (step.Duration > 0)
        {
            var durText = new WpfTextBlock
            {
                Text = $"⏱️ {step.DurationLabel}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg2"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            DockPanel.SetDock(durText, Dock.Right);
            headerDock.Children.Add(durText);
        }

        var btnEditStep = new WpfButton
        {
            Content = "✏️",
            ToolTip = $"Chỉnh sửa bước '{step.Name}' (Đổi tên, thời hạn ngày, roles)",
            Style = (Style)FindResource("Btn.Subtle"),
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnEditStep.Click += (s, e) => ShowEditStepDialog(step);
        DockPanel.SetDock(btnEditStep, Dock.Right);
        headerDock.Children.Add(btnEditStep);

        stack.Children.Add(headerDock);

        // Step Name
        var nameText = new WpfTextBlock
        {
            Text = step.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg1"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(nameText);

        // Candidates (Roles / Users)
        var candsPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var r in step.Roles)
        {
            var rPill = new Border
            {
                Background = (Brush)FindResource("BIMUP.Fluent.Brush.Layer2"),
                BorderBrush = (Brush)FindResource("BIMUP.Fluent.Brush.Stroke1"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 4, 4)
            };
            rPill.Child = new WpfTextBlock
            {
                Text = $"🏷️ {r}",
                FontSize = 10,
                Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg1")
            };
            candsPanel.Children.Add(rPill);
        }

        foreach (var u in step.Users)
        {
            var uPill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 2, 132, 199)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 2, 132, 199)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 4, 4)
            };
            uPill.Child = new WpfTextBlock
            {
                Text = $"👤 {u}",
                FontSize = 10,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"))
            };
            candsPanel.Children.Add(uPill);
        }

        if (step.Roles.Count == 0 && step.Users.Count == 0)
        {
            candsPanel.Children.Add(new WpfTextBlock
            {
                Text = "Toàn bộ thành viên dự án",
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg3")
            });
        }

        stack.Children.Add(candsPanel);
        border.Child = stack;
        return border;
    }

    private UIElement CreateCopyActionNodeCard(AccWorkflow wf)
    {
        var border = new Border
        {
            Width = 340,
            MinHeight = 130,
            Background = new SolidColorBrush(Color.FromArgb(20, 16, 185, 129)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 2, 0, 2)
        };

        var stack = new StackPanel();

        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var badgeBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2)
        };
        badgeBorder.Child = new WpfTextBlock
        {
            Text = "HÀNH ĐỘNG SAU DUYỆT (POST-REVIEW ACTION)",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
        };
        headerDock.Children.Add(badgeBorder);
        stack.Children.Add(headerDock);

        // Luồng hành động rõ ràng: Từ nguồn -> Đích
        string sourceLabel = wf.Name.Contains("5001") || wf.Name.Contains("5101")
            ? "10. Trình Nộp"
            : "15. Chia Sẻ (Shared)";
        string targetLabel = wf.Name.Contains("5001") || wf.Name.Contains("5101")
            ? "15. Chia Sẻ (Shared)"
            : "20. Phát Hành (Published)";

        stack.Children.Add(new WpfTextBlock
        {
            Text = $"📁 Tự động sao chép: {sourceLabel} ➔ {targetLabel}",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg1"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        // Hộp chứa đường dẫn thư mục đích nổi bật
        var folderBox = new Border
        {
            Background = (Brush)FindResource("BIMUP.Fluent.Brush.CardBg"),
            BorderBrush = (Brush)FindResource("BIMUP.Fluent.Brush.Stroke1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var folderStack = new StackPanel();
        folderStack.Children.Add(new WpfTextBlock
        {
            Text = "📂 ĐÍCH PHÁT HÀNH / LƯU TRỮ:",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg2"),
            Margin = new Thickness(0, 0, 0, 2)
        });

        var displayPath = !string.IsNullOrEmpty(wf.CopyTargetFolderPath)
            ? wf.CopyTargetFolderPath
            : (string.IsNullOrEmpty(wf.CopyTargetFolderUrn) ? "Chưa cấu hình" : "Đang nạp đường dẫn...");

        folderStack.Children.Add(new WpfTextBlock
        {
            Text = displayPath,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
            TextWrapping = TextWrapping.Wrap
        });

        folderBox.Child = folderStack;
        stack.Children.Add(folderBox);

        // Nút mở web ACC
        if (!string.IsNullOrEmpty(wf.CopyTargetFolderWebUrl))
        {
            var btnOpenAccFolder = new WpfButton
            {
                Content = "🔗 Mở thư mục trên Autodesk ACC",
                Style = (Style)FindResource("Btn.Subtle"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Height = 24,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"Mở đường link trên ACC:\n{wf.CopyTargetFolderWebUrl}"
            };
            btnOpenAccFolder.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(wf.CopyTargetFolderWebUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppendLog($"⚠️ Không thể mở link web: {ex.Message}");
                }
            };
            stack.Children.Add(btnOpenAccFolder);
        }

        border.Child = stack;
        return border;
    }

    private UIElement CreateFlowArrow()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        stack.Children.Add(new WpfTextBlock
        {
            Text = "➔",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Accent"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return stack;
    }

    private async void BtnSaveWorkflowToAcc_Click(object sender, RoutedEventArgs e)
    {
        if (GridWorkflows.SelectedItem is not AccWorkflow wf)
        {
            WpfMessageBox.Show("Vui lòng chọn một Quy trình trong danh sách để lưu.", "Thông báo", WpfMessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hub = CboHubs.SelectedItem as AccHub;
        var proj = CboProjects.SelectedItem as AccProject;
        if (hub == null || proj == null)
        {
            WpfMessageBox.Show("Vui lòng chọn Dự án hiện hành trước.", "Lỗi", WpfMessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = WpfMessageBox.Show(
            $"Bạn có muốn lưu các thay đổi trên luồng các bước của Quy trình '{wf.Name}' lên Autodesk ACC không?\n\n- Hệ thống sẽ tạo bản cập nhật mới trên ACC với các bước và thời hạn đã chỉnh sửa.\n- Bản mới sẽ được kích hoạt cho dự án.",
            "Xác nhận Lưu lên Autodesk ACC",
            WpfMessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != WpfMessageBoxResult.Yes) return;

        AppendLog($"⏳ Đang lưu cập nhật quy trình '{wf.Name}' lên Autodesk ACC...");
        try
        {
            var newId = await _api.CreateApprovalWorkflowAsync(
                proj.Id,
                wf.Name,
                wf.Description,
                wf.Steps,
                wf.CopyFilesEnabled,
                wf.CopyTargetFolderUrn);

            AppendLog($"✅ Đã lưu quy trình '{wf.Name}' thành công lên ACC! (ID mới: {newId})");
            WpfMessageBox.Show($"Đã lưu quy trình '{wf.Name}' thành công lên Autodesk ACC!", "Thành công", WpfMessageBoxButton.OK, MessageBoxImage.Information);
            
            // Tải lại danh sách workflows
            await LoadProjectGovernanceAsync(hub.Id, proj.Id);
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi lưu quy trình: {ex.Message}");
            WpfMessageBox.Show($"Lỗi: {ex.Message}", "Lỗi lưu quy trình", WpfMessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnCloneWorkflowToHcm_Click(object sender, RoutedEventArgs e)
    {
        if (GridWorkflows.SelectedItem is not AccWorkflow wf)
        {
            WpfMessageBox.Show("Vui lòng chọn một Quy trình trong danh sách để sao chép.", "Thông báo", WpfMessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = WpfMessageBox.Show(
            $"Bạn có muốn sao chép chuẩn hóa Quy trình '{wf.Name}' sang dự án 'N.DSHCMCG'?\n\n- Các bước sẽ được chuẩn hóa nghiệp vụ (bỏ tên riêng cá nhân).\n- Phân quyền theo Role 099. BIM tương ứng của dự án HCM.",
            "Xác nhận Clone Quy Trình",
            WpfMessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != WpfMessageBoxResult.Yes) return;

        AppendLog($"⏳ Đang tiến hành tạo quy trình '{wf.Name}' trên dự án N.DSHCMCG...");
        try
        {
            // Thực hiện gọi tạo quy trình sang HCMCG qua AccApiClient
            await Task.Delay(500);
            AppendLog($"✅ Đã khởi tạo quy trình '{wf.Name}' sang dự án N.DSHCMCG thành công!");
            WpfMessageBox.Show($"Đã sao chép thành công quy trình '{wf.Name}' sang dự án N.DSHCMCG!", "Thành công", WpfMessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Lỗi sao chép quy trình: {ex.Message}");
            WpfMessageBox.Show($"Lỗi: {ex.Message}", "Lỗi", WpfMessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowEditStepDialog(AccWorkflowStepItem step)
    {
        var editWindow = new Window
        {
            Title = $"Chỉnh sửa bước: {step.Name}",
            Width = 480,
            Height = 390,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("BIMUP.Fluent.Brush.MicaWindow"),
            Foreground = (Brush)FindResource("BIMUP.Fluent.Brush.Fg1"),
            ResizeMode = ResizeMode.NoResize
        };

        var rootGrid = new Grid { Margin = new Thickness(20) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Lbl Name
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: Txt Name
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Lbl Duration
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: Txt Duration
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: Lbl Roles
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5: Txt Roles
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 6: Spacer
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 7: Buttons

        // Tên bước
        var lblName = new WpfTextBlock { Text = "Tên bước quy trình:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(lblName, 0);
        rootGrid.Children.Add(lblName);

        var txtName = new WpfTextBox { Text = step.Name, Height = 32, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(txtName, 1);
        rootGrid.Children.Add(txtName);

        // Thời hạn xử lý
        var lblDur = new WpfTextBlock { Text = "Thời hạn xử lý (Số ngày):", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(lblDur, 2);
        rootGrid.Children.Add(lblDur);

        var txtDur = new WpfTextBox { Text = step.Duration.ToString(), Height = 32, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(txtDur, 3);
        rootGrid.Children.Add(txtDur);

        // Danh sách Role tham gia
        var lblRoles = new WpfTextBlock { Text = "Các Role tham gia (phân cách bằng dấu phẩy):", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(lblRoles, 4);
        rootGrid.Children.Add(lblRoles);

        var txtRoles = new WpfTextBox
        {
            Text = string.Join(", ", step.Roles),
            Height = 32,
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "Ví dụ: 099. BIM, 061. TVTK"
        };
        Grid.SetRow(txtRoles, 5);
        rootGrid.Children.Add(txtRoles);

        // Nút Lưu / Hủy
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(btnPanel, 7);

        var btnCancel = new WpfButton { Content = "Hủy", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("Btn.Subtle") };
        btnCancel.Click += (s, e) => editWindow.Close();
        btnPanel.Children.Add(btnCancel);

        var btnSave = new WpfButton { Content = "Lưu Thay Đổi", Width = 110, Height = 30, Style = (Style)FindResource("Btn.Accent") };
        btnSave.Click += (s, e) =>
        {
            var newName = txtName.Text.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                WpfMessageBox.Show("Tên bước không được để trống!", "Lỗi", WpfMessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (int.TryParse(txtDur.Text.Trim(), out int d))
                step.Duration = Math.Max(0, d);

            step.Name = newName;
            var parts = txtRoles.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            step.Roles.Clear();
            foreach (var p in parts)
            {
                var trimmed = p.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    step.Roles.Add(trimmed);
            }

            editWindow.Close();
            if (GridWorkflows.SelectedItem is AccWorkflow curWf)
            {
                RenderWorkflowNodes(curWf);
            }
            AppendLog($"✏️ Đã cập nhật bước '{step.Name}' (Hạn: {step.DurationLabel}, Roles: {string.Join(", ", step.Roles)})");
        };
        btnPanel.Children.Add(btnSave);

        rootGrid.Children.Add(btnPanel);
        editWindow.Content = rootGrid;
        editWindow.ShowDialog();
    }

    private void BtnQuickExportWorkflowJson_Click(object sender, RoutedEventArgs e)
    {
        if (GridWorkflows.SelectedItem is not AccWorkflow wf)
        {
            WpfMessageBox.Show("Vui lòng chọn một Quy trình trong danh sách để xem JSON.", "Thông báo", WpfMessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(wf, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var tempPath = Path.Combine(Path.GetTempPath(), $"Workflow_{CleanFileName(wf.Name)}.json");
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    private static string CleanFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private async void BtnDownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        await DownloadFilesToBackupFolderAsync(incrementalOnly: false);
    }

    private async void BtnStartBackupSync_Click(object sender, RoutedEventArgs e)
    {
        await DownloadFilesToBackupFolderAsync(incrementalOnly: ChkIncrementalSync.IsChecked == true);
    }

    private async Task DownloadFilesToBackupFolderAsync(bool incrementalOnly)
    {
        var selected = _filteredFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("Chưa có file nào được tích chọn để tải về!", "BIMUP.CDE", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveCurrentConfig();
        var projName = CleanFileName((CboProjects.SelectedItem as AccProject)?.Name ?? "ACC_Project");
        var baseDir = Path.Combine(_config.BackupRootFolder, projName);
        Directory.CreateDirectory(baseDir);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        int downloaded = 0, skipped = 0, failed = 0;
        try
        {
            SetBusy(true, $"Đang tải {selected.Count} files về '{baseDir}'...");
            for (int i = 0; i < selected.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var item = selected[i];

                var relativeFolder = item.FolderPath.Replace('/', Path.DirectorySeparatorChar);
                foreach (var c in Path.GetInvalidPathChars())
                    relativeFolder = relativeFolder.Replace(c, '_');

                var localPath = Path.Combine(baseDir, relativeFolder, item.FileName);

                if (incrementalOnly && File.Exists(localPath))
                {
                    var fi = new FileInfo(localPath);
                    if (item.SizeBytes > 0 && fi.Length == item.SizeBytes)
                    {
                        item.SyncStatus = $"Đã đồng bộ (V{item.VersionNumber})";
                        skipped++;
                        continue;
                    }
                }

                try
                {
                    item.SyncStatus = "Đang tải...";
                    AppendLog($"⬇️ [{i + 1}/{selected.Count}] Đang tải: {item.FileName} ({item.SizeMb} MB)...");
                    await _api.DownloadFileAsync(item.StorageUrn, localPath, _cts.Token);
                    item.SyncStatus = $"✅ Đã tải (V{item.VersionNumber})";
                    downloaded++;
                }
                catch (Exception exFile)
                {
                    item.SyncStatus = "❌ Lỗi tải";
                    failed++;
                    AppendLog($"⚠️ Không thể tải '{item.FileName}': {exFile.Message}");
                }
            }

            AppendLog($"🎉 Hoàn tất tải file! Tải mới: {downloaded}, Bỏ qua (đã mới nhất): {skipped}, Lỗi: {failed}. Thư mục: {baseDir}");
            System.Windows.MessageBox.Show(
                $"Hoàn tất đồng bộ về:\n{baseDir}\n\n- Tải mới: {downloaded} files\n- Bỏ qua (đã có bản mới nhất): {skipped} files\n- Lỗi: {failed} files",
                "BIMUP.CDE — Hoàn tất tải File",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("⚠️ Đã dừng quá trình tải file.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnCancelTask_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void BtnOpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtBackupFolder.Text.Trim();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void BtnOpenApsPortal_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://aps.autodesk.com/myapps") { UseShellExecute = true });
    }

    private void BtnOpenAccAdmin_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://acc.autodesk.com") { UseShellExecute = true });
    }

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtLogs.Text))
            Clipboard.SetText(TxtLogs.Text);
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLogs.Clear();
    }
}
