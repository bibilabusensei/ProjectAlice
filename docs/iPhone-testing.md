# Alice·空间伙伴：iPhone 测试说明

本版专注 LiDAR 和角色基础功能，不运行单摄回退、Android 扩展或 AI 接口。
发布包来自 macOS / Xcode 的 Release / ios-arm64 构建。下载未签名 IPA 后自行签名安装。
源码和发布包没有私钥、Apple 凭据、API 密钥或受限第三方模型。

## 三步开始

1. 允许相机，缓慢扫描地板或桌面，点击蓝色区域放置内置“小光”。
2. 点“角色动作”试挥手/看向我；再点同一平面选目标，在动作菜单确认移动。
3. 点“导入角色”，从“文件”选 USDZ / DAE / OBJ / GLB / VRM；“更多”里调整大小、转身和查看帮助。

阿罗娜 VRM 可以作为基础模型导入，但本版不驱动她的骨骼、表情或裙摆物理。
没有行走动画的模型只做空间平移。导入不需要 API，画面不会离开设备。

## 请重点测试

- 竖屏、左横屏、右横屏：预览不倒置、不拉伸，点击位置正确，底部按钮不挡住主要画面。
- 第一次放置、再次选目标、确认移动、停止动作；不同平面/太远/扫描不完整时应有提示。
- 小光挥手和迈步；导入阿罗娜后贴图、初始姿态、脚底、高度和朝向。
- 导入取消、错误格式、无网格、损坏文件和超过 80 MB 的文件：应保留原角色而不是崩溃。
- 调整高度、转身、恢复内置角色、重新扫描；导入角色不应偷偷播放动画。
- 家具遮挡、切后台、锁屏后恢复、相机权限拒绝后到设置再允许。

移动只做保守扫描范围检查，不保证导航或现实安全。水平面可能是桌面而不是地板。
透明、镜面、低纹理、暗光、快速移动和未扫描物体都可能导致追踪与网格误差。

## 反馈模板

```text
设备：iPhone 15 Pro
iOS 完整版本：
发布页版本 / 构建号：
模型来源及格式：
屏幕方向：竖屏 / 左横屏 / 右横屏
操作顺序：
预期与实际效果：
是否重复发生：
录屏或截图：
```

## 开发验证

```powershell
dotnet run --project AliceVision.Tests/AliceVision.Tests.csproj --configuration Release
dotnet run --project AliceVision.ModelTests/AliceVision.ModelTests.csproj --configuration Release
dotnet run --project AliceVision.ModelTests/AliceVision.ModelTests.csproj -- --inspect-model "你的模型.vrm"
```

Windows 的模型解析/测试成功不等于手机材质渲染成功。iPhone 原生包由 GitHub Actions 构建。
`ProjectAlice.sln` 保留旧 Windows / Android 工程；iPhone 使用 `ProjectAlice.iOS.sln`。
不修改 `lingdongdao` 或其他无关工程。当前尚不能在本机验证用户报告的 iOS 27。
