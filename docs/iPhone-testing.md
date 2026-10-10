# iPhone LiDAR 测试说明

本轮只验证后置相机、ARKit 追踪、水平面、LiDAR 深度与场景网格，以及空间标记的放置和遮挡。
只接受支持 ARKit 场景重建的 LiDAR 设备，不运行单摄回退算法。

## 下载与签名

发布包来自 GitHub Actions 的 macOS Release / ios-arm64 构建，不是模拟器产物。
下载 `AliceVision-iPhone-unsigned.ipa` 后，用你自己的证书和描述文件重新签名再安装。
源码和发布包不包含私钥、Apple 登录凭据、描述文件或网络服务密钥。

发布页同时提供 SHA-256、构建工具链、源码提交和构建日志。
构建和结构检查通过不代表已完成真机测试，也不能保证所有新系统行为完全无 bug。
用户报告的目标是 iPhone 15 Pro / iOS 27，当前暂无该设备可运行验证。

## 测试顺序

1. 首次打开允许相机权限，在光线充足的房间缓慢扫描地板或桌面。
2. 等待蓝色候选水平面出现，然后点击放置约 25 cm 高的三维标记。
3. 左右移动手机，标记应该固定在原位置；再次点击其他蓝色区域应移动标记。
4. 分别测试竖屏、左横屏、右横屏：画面不倒置、不拉伸，点击位置与标记位置一致。
5. 打开“调试数据”，检查 LiDAR 深度帧尺寸和网格数量；用真实物体挡住标记检查遮挡。
6. 切后台、锁屏后返回，检查恢复或重新定位提示；“重新扫描”应清除旧标记并重新建图。
7. 系统设置里拒绝相机后重开应用，应有权限提示；重新允许后返回应用应继续。

水平面可能是桌面，不等于地板。透明、镜面、低纹理、暗光或快速移动环境可能导致
追踪和网格误差。此版本不应用于测量、安全避障或现实设备自动控制。

## 反馈模板

```text
设备：iPhone 15 Pro
iOS 完整版本：
发布页版本 / 构建号：
屏幕方向：竖屏 / 左横屏 / 右横屏
操作顺序：
预期效果：
实际效果：
是否可重复：
录屏或截图：
```

## 开发验证

Windows 可运行共享测试，不可独立编译 iPhone 原生设备包：

```powershell
dotnet run --project AliceVision.Tests/AliceVision.Tests.csproj --configuration Release
dotnet run --project AliceVision/AliceVision.csproj --configuration Release
```

`ProjectAlice.sln` 保留原有 Windows / Android 工程；iPhone 使用单独的 `ProjectAlice.iOS.sln`。
`FloorRegionEstimator` 是亮度边缘实验，不是单目深度、地板语义识别或 SLAM，不接入 iOS。
