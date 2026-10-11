# Alice·空间伙伴：iPhone 测试说明

本版专注 LiDAR、用户模型骨骼动作与可选视觉 AI，不运行单摄回退或 Android 扩展。
发布包来自 macOS / Xcode 的 Release / ios-arm64 构建。下载未签名 IPA 后自行签名安装。
源码和发布包没有私钥、Apple 凭据、API 密钥或受限第三方模型。

## 三步开始

1. 允许相机，缓慢扫描地板或桌面，点击蓝色区域放置内置“小光”。
2. 点“角色动作”试挥手/看向我；再点同一平面选目标，在动作菜单确认移动。
3. 点“导入角色”，从“文件”选 USDZ / DAE / OBJ / GLB / VRM；“更多”里调整大小、转身和查看帮助。

请导入你自己准备的新模型。带标准 VRM 人形映射的模型可做骨骼动作；不能识别人形映射的模型只开放可支持动作。
导入不需要 API；不开 AI 时画面不会离开设备。自主模式见 [AI 与活动区域](ai-companion.md)。

## 请重点测试

- 竖屏、左横屏、右横屏：预览不倒置、不拉伸，点击位置正确，底部按钮不挡住主要画面。
- 第一次放置、再次选目标、确认移动、停止动作；不同平面/太远/扫描不完整时应有提示。
- 小光与自己的模型：贴图、初始姿态、脚底、高度、朝向；招手应驱动手臂，而不是整个模型旋转。
- 左右招手、欢迎、点头、鞠躬、舞动；连续切动作与急停后是否恢复初始姿态。
- 导入取消、错误格式、无网格、损坏文件、超过 1 GB 或超几何/纹理预算：应保留原角色而不是崩溃。
- 调整高度、转身、恢复内置角色、重新扫描；导入角色不应偷偷播放动画。
- 家具遮挡、切后台、锁屏后恢复、相机权限拒绝后到设置再允许。
- 划绿色活动区、添加红色禁区；整个脚部及每段路径不得穿出区域或穿越禁区。
- 启动 AI 后，急停、后台、改区域/模型、追踪不稳应停止上传/拒绝旧回复，不自动恢复。
- 错 Key、非视觉模型、超时、限流应显示错误，3 次连续失败或 429 应暂停；不能执行 API 返回的任意坐标或代码。

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
dotnet run --project AliceVision.AgentTests/AliceVision.AgentTests.csproj --configuration Release
dotnet run --project AliceVision.ModelTests/AliceVision.ModelTests.csproj -- --inspect-model "你的模型.vrm"
```

Windows 的模型解析/测试成功不等于手机材质渲染成功。iPhone 原生包由 GitHub Actions 构建。
`ProjectAlice.sln` 保留旧 Windows / Android 工程；iPhone 使用 `ProjectAlice.iOS.sln`。
不修改 `lingdongdao` 或其他无关工程。当前尚不能在本机验证用户报告的 iOS 27。
