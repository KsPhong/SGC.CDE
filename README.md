# SGC CDE Studio

**SGC CDE Studio** là ứng dụng desktop quản lý môi trường dữ liệu chung (Common Data Environment - CDE) kết nối với nền tảng **Autodesk Construction Cloud (ACC)**, **BIM 360** và **Autodesk Platform Services (APS)**.

Ứng dụng được thiết kế theo phong cách hiện đại (WinUI 3 / Fluent 2 & UI/UX Pro Max), tối ưu hóa hiệu năng nạp dữ liệu phân tầng (lazy loading) và tích hợp sẵn chuẩn giao thức **MCP (Model Context Protocol)** để AI Agent có thể điều khiển trực tiếp.

---

## 🚀 Tính năng nổi bật

- **Tài liệu & File (CDE Explorer)**:
  - Cây thư mục dự án ACC phân cấp mượt mà, chống giật nháy (flicker-free).
  - Bảng danh sách thư mục con và file chi tiết với thiết kế tối giản, không dòng kẻ ô thô cứng, khoảng cách dòng thoáng đãng.
  - Hiển thị badge trạng thái duyệt hồ sơ (`APPROVED`, `IN_REVIEW`, `REJECTED`) và phiên bản file (`V1`, `V2`,...).
  - Tìm kiếm nhanh, lọc theo định dạng file (`RVT`, `DWG`, `NWD`, `IFC`, `PDF`,...).
  - Xuất báo cáo cấu trúc cây thư mục và danh sách file ra file Excel (.CSV).
- **Quản trị Dự án (Governance)**:
  - Danh sách Roles công nghiệp ACC (Industry Roles).
  - Danh sách Thành viên dự án và Hub (Project & Hub Members).
  - Quét và tổng hợp phân quyền truy cập thư mục chi tiết (Folder Permissions).
  - Xuất báo cáo Roles & Members ra file .CSV.
- **Quy trình Phê duyệt & Đợt duyệt (Workflows & Reviews)**:
  - Danh sách các Approval Workflows được cấu hình trong ACC Docs.
  - Theo dõi tiến độ các đợt trình duyệt hồ sơ (Reviews), bước duyệt hiện tại, người xử lý kế tiếp và danh sách file đính kèm.
- **Vấn đề & Phát hành (Issues & Transmittals)**:
  - Xem danh sách Issues của dự án.
  - Quản lý các gói hồ sơ phát hành chính thức (Transmittals).
- **Sao lưu & Đồng bộ Tự động (Backup & Incremental Sync)**:
  - Tự động tái tạo cây thư mục trên máy tính giống hệt ACC.
  - Cơ chế đồng bộ tăng trưởng (Incremental Sync): chỉ tải về các file mới hoặc có version mới hơn.
- **Tích hợp MCP AI Bridge**:
  - Giao tiếp qua Named Pipe chuẩn (`BIMUP.Mcp.cde`).
  - Cho phép AI Agent tự động truy vấn trạng thái, duyệt cây thư mục, kiểm tra phân quyền và điều khiển CDE.

---

## 🛠️ Công nghệ sử dụng

- **Nền tảng**: .NET 10 (WPF - Windows Presentation Foundation)
- **UI Framework**: `Wpf.Ui` (v4.3.0) & hệ thống Design Tokens Fluent 2
- **Kiến trúc MVVM**: `CommunityToolkit.Mvvm` (v8.4.0)
- **Giao thức AI**: Named Pipe IPC / Model Context Protocol (MCP)

---

## 💻 Hướng dẫn Build & Chạy

### Yêu cầu môi trường
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Build mã nguồn
```bash
dotnet build src/BIMUP.CDE/BIMUP.CDE.csproj
```

### Chạy ứng dụng
```bash
dotnet run --project src/BIMUP.CDE/BIMUP.CDE.csproj
```

### Xuất bản (Publish Release)
```bash
dotnet publish src/BIMUP.CDE/BIMUP.CDE.csproj -c Release -r win-x64 --self-contained false -o "%LOCALAPPDATA%\Programs\BIMUP\CDE"
```

---

## 👥 Thành viên & Đóng góp
Dự án được bảo trì và phát triển bởi đội ngũ SGC.
Mọi đóng góp và pull request đều được hoan nghênh.
