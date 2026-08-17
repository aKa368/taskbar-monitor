# GitHub Secrets cho code signing Taskbar Monitor

## Tổng quan

Workflow `.github/workflows/build-and-test.yml` hiện giữ hai luồng riêng. Push vào `main` và pull request chỉ build/test; bước ký số chỉ chạy khi push một tag phát hành có dạng `vMAJOR.MINOR.PATCH`, ví dụ `v1.0.2`. Điều này ngăn mã nguồn từ pull request không đáng tin cậy tiếp cận private key.

Release job thực hiện restore, đối chiếu tag với `<Version>` trong `src/TaskbarMonitor.csproj`, publish self-contained `win-x64`, compile Inno Setup, ký installer cuối cùng, verify chữ ký, tạo SHA-256 và upload installer đã xác minh.

> **Không chạy signing từ pull request.** Không dùng secret trong job build/test thông thường. Không upload PFX, password hoặc file chứa private key như artifact.

## 1. Chuẩn bị certificate

Sử dụng certificate Authenticode Code Signing công khai từ CA uy tín, Microsoft Artifact Signing, certificate store hoặc hardware token. Không dùng certificate tự ký cho phát hành công khai.

Phiên bản workflow hiện tại dùng PFX vì GitHub-hosted Windows runner là máy ephemeral. PFX chỉ tồn tại tạm thời trong `$env:RUNNER_TEMP`; workflow xóa file này trong bước `if: always()` sau khi ký hoặc khi pipeline thất bại.

Nếu nhà cung cấp cấp certificate dưới dạng `.pfx`, cần có:

| Giá trị | Mục đích |
|---|---|
| File PFX | Chứa certificate và private key được mã hóa |
| PFX password | Mở private key trong bước ký |
| Publisher name | Đối chiếu danh tính signer sau khi ký |
| Certificate thumbprint | Pin đúng certificate, tránh ký nhầm bằng certificate khác |
| RFC 3161 timestamp URL | Giúp chữ ký tiếp tục xác minh sau khi certificate hết hạn |

## 2. Tạo Base64 secret cho PFX trên Windows

Thực hiện trên máy quản lý certificate, không thực hiện trên runner công khai và không commit file PFX vào Git. PowerShell dưới đây chỉ đưa chuỗi Base64 vào clipboard; hãy dán ngay vào GitHub Secret rồi xóa clipboard.

```powershell
$pfxPath = 'C:\Secure\TaskbarMonitor-CodeSigning.pfx'
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath)) | Set-Clipboard
```

Để lấy thumbprint và subject:

```powershell
Get-PfxCertificate -FilePath 'C:\Secure\TaskbarMonitor-CodeSigning.pfx' |
  Select-Object Subject, Thumbprint, NotBefore, NotAfter
```

Để tránh sai thumbprint, xóa khoảng trắng khi lưu secret. Không lưu private key hoặc password vào `appsettings`, `.env`, workflow YAML hoặc issue/comment.

## 3. Tạo GitHub Environment bảo vệ release signing

Trong repository, mở **Settings → Environments → New environment** và tạo environment có tên `release-signing`. Nên bật required reviewers và giới hạn deployment branch/tag cho các tag phát hành `v*` nếu chính sách repository hỗ trợ.

Trong **Settings → Secrets and variables → Actions → Environments → release-signing**, tạo các Environment secrets sau:

| Secret | Bắt buộc | Giá trị |
|---|---:|---|
| `CODESIGN_PFX_BASE64` | Có | Toàn bộ nội dung PFX đã Base64-encode |
| `CODESIGN_PFX_PASSWORD` | Có | Mật khẩu mở PFX |
| `CODESIGN_TIMESTAMP_URL` | Khuyến nghị | URL RFC 3161, ví dụ `http://timestamp.digicert.com` |
| `CODESIGN_EXPECTED_PUBLISHER` | Khuyến nghị | Chuỗi phải xuất hiện trong Subject/simple name của signer, ví dụ `aKa368` |
| `CODESIGN_EXPECTED_THUMBPRINT` | Khuyến nghị | Thumbprint certificate ký, không có khoảng trắng |

Nếu dùng GitHub Organization, có thể lưu các secret dùng chung ở Organization level nhưng phải giới hạn repository được phép truy cập. Không dùng `Repository secrets` rộng rãi nếu chỉ một environment phát hành cần quyền ký.

## 4. Liên kết job với environment

Để required reviewers của environment thực sự bảo vệ secret, thêm `environment: release-signing` vào job `release` trong `.github/workflows/build-and-test.yml`:

```yaml
  release:
    name: Package and sign installer
    environment: release-signing
    if: github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')
    needs: test
    runs-on: windows-latest
```

Nếu repository không muốn approval thủ công, vẫn nên giữ environment để secrets không xuất hiện trong job thông thường. Khi bật approval, tag workflow sẽ chờ reviewer xác nhận trước khi runner nhận được secrets.

## 5. Kiểm tra cấu hình trước khi phát hành

Đảm bảo `<Version>` trong `src/TaskbarMonitor.csproj` bằng version của tag. Ví dụ:

```xml
<Version>1.0.2</Version>
```

Sau đó tạo tag trên commit đã build/test:

```powershell
git fetch origin main
git checkout main
git pull --ff-only origin main
git tag -a v1.0.2 -m 'Release TaskbarMonitor v1.0.2'
git push origin v1.0.2
```

Workflow sẽ dừng trước signing nếu tag không khớp project version. Khi signing job chạy, cần thấy các kết quả sau:

1. `dotnet restore --locked-mode` thành công.
2. `dotnet publish` tạo `release-1.0.2/staging/TaskbarMonitor`.
3. Inno Setup tạo `release-1.0.2/TaskbarMonitor-Setup-v1.0.2-win-x64.exe`.
4. SignTool ký bằng SHA-256 và timestamp RFC 3161.
5. `signtool verify /pa /v` thành công.
6. `Get-AuthenticodeSignature` trả về `Valid` và có timestamp certificate.
7. Publisher và thumbprint khớp secret kỳ vọng nếu đã cấu hình.
8. Artifact upload chỉ chứa installer, file `.sha256` và `signing-summary.json`.

## 6. Quy tắc bảo vệ secret

Không echo các biến `CODESIGN_*` vào log. Không sử dụng `Write-Host $env:CODESIGN_PFX_PASSWORD`, không in command line đã expand, không tạo debug dump của environment và không đưa PFX vào cache. GitHub masking giảm nguy cơ lộ secret trong log nhưng không thay thế cho việc tránh đưa secret vào output.

Nếu workflow thất bại, tải log để kiểm tra lỗi nhưng không tải hoặc lưu PFX. Nếu nghi ngờ password/private key đã lộ, revoke certificate hoặc rotate private key theo quy trình của CA trước khi chạy release tiếp theo.

Không sửa installer sau bước signing. Nếu thay đổi file, phải compile lại hoặc ký lại, rồi verify và tạo hash mới.

## 7. Phương án không dùng PFX

Certificate store, hardware token hoặc managed signing service là lựa chọn tốt hơn khi tổ chức có hạ tầng tương ứng. Khi đó, thay bước decode PFX bằng bước setup của nhà cung cấp và gọi helper với:

```powershell
pwsh -NoProfile -File .\jobs\Sign-Release.ps1 `
  -ArtifactPath $installer `
  -CertificateThumbprint $env:CODESIGN_CERT_THUMBPRINT `
  -TimestampUrl $env:CODESIGN_TIMESTAMP_URL `
  -ExpectedPublisher $env:CODESIGN_EXPECTED_PUBLISHER `
  -ExpectedThumbprint $env:CODESIGN_EXPECTED_THUMBPRINT
```

Không cấu hình đồng thời PFX và thumbprint. Helper sẽ từ chối khi có cả hai hoặc không có nguồn certificate nào.

## 8. SmartScreen

Ký số làm Windows hiển thị publisher đã xác minh và bảo vệ tính toàn vẹn file, nhưng không bảo đảm cảnh báo SmartScreen biến mất ngay với file mới. SmartScreen còn dùng file-hash reputation và publisher reputation; reputation cần tích lũy từ các lượt tải sạch. Vì vậy mỗi release phải dùng certificate nhất quán, phân phối installer từ kênh chính thức và công bố SHA-256.

## Tài liệu tham khảo

- [SmartScreen reputation for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)
- [SignTool.exe](https://learn.microsoft.com/en-us/dotnet/framework/tools/signtool-exe)
- [Time Stamping Authenticode Signatures](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)
- [Use SignTool to Verify a File Signature](https://learn.microsoft.com/en-us/windows/win32/seccrypto/using-signtool-to-verify-a-file-signature)
