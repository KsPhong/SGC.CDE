using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BIMUP.CDE.Models;

public class AccConfig
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool UseThreeLeggedAuth { get; set; } = false;
    public string CallbackUrl { get; set; } = "http://localhost:8080/api/auth/callback";
    public string BackupRootFolder { get; set; } = @"D:\ACC_Backup";
    public bool IncludeCustomAttributes { get; set; } = true;

    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMUP", "cde_settings.json");

    public static AccConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AccConfig>(json) ?? new AccConfig();
            }
        }
        catch
        {
            // Ignore corrupt config
        }
        return new AccConfig();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}

public class AccHub
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "US";
    public string Display => $"{Name} ({Region})";
    public override string ToString() => Display;
}

public class AccProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProjectType { get; set; } = "ACC";
    public string Display => $"{Name} [{ProjectType}]";
    public override string ToString() => Display;
}

public class AccFolder : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private int _objectCount;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string ParentId { get; set; } = "";

    public int ObjectCount
    {
        get => _objectCount;
        set { _objectCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasObjects)); }
    }

    public bool HasObjects => ObjectCount > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool Hidden { get; set; }
    public string CreateUserName { get; set; } = "";
    public string CreateTime { get; set; } = "";
    public string LastModifiedUserName { get; set; } = "";
    public string LastModifiedTime { get; set; } = "";
    public ObservableCollection<AccFolder> Children { get; } = new();

    public string Display => Name;
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AccFileItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _syncStatus = "Sẵn sàng";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string FileName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string FileType { get; set; } = "";
    public int VersionNumber { get; set; } = 1;
    public string VersionDisplay => $"V{VersionNumber}";
    public double SizeMb { get; set; }
    public string SizeDisplay => SizeMb > 0 ? $"{SizeMb:0.##} MB" : "—";
    public long SizeBytes { get; set; }
    public string CreateUserName { get; set; } = "";
    public string CreateTime { get; set; } = "";
    public string LastModifiedUserName { get; set; } = "";
    public string LastModifiedTime { get; set; } = "";
    public string ReviewState { get; set; } = "N/A";
    public string CustomAttributesSummary { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string VersionUrn { get; set; } = "";
    public string StorageUrn { get; set; } = "";
    public string DerivativeUrn { get; set; } = "";

    public string SyncStatus
    {
        get => _syncStatus;
        set { _syncStatus = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AccRole
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MemberGroupId { get; set; } = "";
    public string ServicesSummary { get; set; } = "";
}

public class AccMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string RolesSummary { get; set; } = "";
    public string AccessLevel { get; set; } = "";
    public string Status { get; set; } = "";
    public string ProductsSummary { get; set; } = "";
    public string AutodeskId { get; set; } = "";
    public string AddedOn { get; set; } = "";
    public string Scope { get; set; } = "Dự án";
}

public class AccCompany
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Trade { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
}

public class AccPermission
{
    public string FolderPath { get; set; } = "";
    public string SubjectType { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public string EmailOrInfo { get; set; } = "";
    public string PermissionLevel { get; set; } = "";
    public string ActionsSummary { get; set; } = "";
    public string InheritActionsSummary { get; set; } = "";
    public string SubjectId { get; set; } = "";
}

public class AccWorkflowStepItem
{
    public int Order { get; set; }
    public string StepId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // INITIATOR, REVIEWER, APPROVER
    public string TypeBadge => Type switch
    {
        "INITIATOR" => "Người Khởi Tạo",
        "REVIEWER" => "Kiểm Tra (Reviewer)",
        "APPROVER" => "Phê Duyệt (Approver)",
        _ => Type
    };
    public string TypeColor => Type switch
    {
        "INITIATOR" => "#4F5BD5", // Brand Accent / Purple
        "REVIEWER" => "#0284C7",  // Sky Blue
        "APPROVER" => "#10B981",  // Emerald Green
        _ => "#6B7280"
    };
    public int Duration { get; set; }
    public string DueDateType { get; set; } = "CALENDAR_DAY";
    public string DurationLabel => Duration > 0 ? $"{Duration} ngày" : "Không giới hạn";
    public List<string> Roles { get; set; } = new();
    public List<string> Users { get; set; } = new();
    public string CandidatesSummary => string.Join(", ", Roles.Concat(Users));
}

public class AccWorkflow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public int StepCount { get; set; }
    public string StepsSummary { get; set; } = "";
    public string CandidatesSummary { get; set; } = "";
    public string ApprovalOptionsSummary { get; set; } = "";
    public string CopyFilesInfo { get; set; } = "";
    public string CopyTargetFolderUrn { get; set; } = "";
    public string CopyTargetFolderPath { get; set; } = "";
    public string CopyTargetFolderWebUrl { get; set; } = "";
    public bool CopyFilesEnabled { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string CreatedBy { get; set; } = "Hệ thống CDE";
    public Dictionary<string, string> StepNamesById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AccWorkflowStepItem> Steps { get; set; } = new();
}

public class AccReview
{
    public string Id { get; set; } = "";
    public int SequenceId { get; set; }
    public string Name { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public string Status { get; set; } = "";
    public string CurrentStepName { get; set; } = "";
    public string CurrentStepDueDate { get; set; } = "";
    public string NextActionBy { get; set; } = "";
    public int FilesCount { get; set; }
    public string FilesSummary { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
}

public class AccTransmittal
{
    public string Id { get; set; } = "";
    public int SequenceId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int DocsCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string RecipientsSummary { get; set; } = "";
}

public class AccIssue
{
    public string Id { get; set; } = "";
    public int DisplayId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string IssueType { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public string DueDate { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string LocationDetails { get; set; } = "";
}
