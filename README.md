# Project Alice

## 当前优先：iPhone LiDAR 测试版

现在优先开发 `AliceVision.iOS`：原生 ARKit 后置相机预览、水平面、LiDAR 场景网格与深度、
真实物体遮挡、点击放置空间标记。目标测试设备为 iPhone 15 Pro。**单摄算法暂不开发，iOS 版不调用下面的边缘估算算法。**

打开 `ProjectAlice.iOS.sln` 查看 iPhone 项目。GitHub Actions 使用 macOS + Xcode 构建实际
设备 ARM64 Release IPA，通过包检查后发布 GitHub Release。IPA 未签名，安装前需要你重新签名。

相机画面仅在本机处理。本轮不包含 AI 对话、角色模型、家具语义识别或自主行走。
最低系统 iOS 15；当前使用 Xcode 26.2 / .NET 10.0.103。新系统按 API 向后兼容方式运行，
用户报告的 iOS 27 尚未完成真机验证。

详见 [iPhone 测试与反馈说明](docs/iPhone-testing.md)。`lingdongdao` 及其他无关项目不属于
Project Alice，不修改、不移动、不上传。

## 保留的早期 Android 原型

Project Alice 是一个面向普通单摄 Android 设备的空间视觉实验项目。当前版本为
`0.1.0`，目标是先跑通“相机画面 → 灰度帧 → 粗略地面候选 → 实时覆盖层”链路。

## 当前已经实现

- 使用 Android Camera2 读取后置摄像头画面；
- 读取 `YUV_420_888` 的亮度平面，不需要把整帧转换成彩色图；
- 处理 Android 相机的行步幅、像素步幅和 0°/90°/180°/270°旋转；
- 用水平边界强度与方向一致性估算粗略地面起始线；
- 对连续帧结果进行平滑，减少覆盖线抖动；
- 在相机预览中用绿色或橙色覆盖候选地面区域；
- 每约 180ms 分析一帧，降低低端手机的功耗和发热；
- 22 项不依赖手机的旋转、步幅、边界与平滑测试。

## 重要限制

当前算法只是第一阶段的轻量几何启发式算法，不是单目深度模型，也不是 SLAM。
它适合验证 Android 实时视觉链路，但暂时不能提供真实距离、稳定世界坐标或可靠避障，
更不能直接用来控制角色在房间中行走。后续会在这个接口后加入深度估计和相机位姿。

## 项目结构

```text
ProjectAlice.sln
AliceVision.Core/       跨平台算法、帧转换和时序平滑
AliceVision.Android/    Camera2 真机应用与覆盖层
AliceVision.Tests/      无第三方测试框架的算法测试程序
AliceVision/            Windows 控制台冒烟测试入口
```

## 在 Visual Studio 中测试 Android

1. 打开 `ProjectAlice.sln`。
2. 将 `AliceVision.Android` 设为启动项目。
3. 在 Android 手机中启用开发者选项和 USB 调试。
4. 用 USB 连接手机，在 Visual Studio 顶部选择该设备。
5. 点击运行，首次启动时允许相机权限。
6. 将摄像头对准墙面与地面的交界处，观察覆盖线和可信度。

绿色表示算法找到较强的候选边界；橙色表示画面信息不足，正在使用安全回退位置。

## 命令行验证

```powershell
dotnet run --project .\AliceVision.Tests\AliceVision.Tests.csproj --configuration Release
dotnet build .\ProjectAlice.sln --configuration Debug
```

生成的调试 APK 位于：

```text
AliceVision.Android\bin\Debug\net10.0-android\com.projectalice.vision-Signed.apk
```

Android 原型保留，但当前不扩展。等 iPhone LiDAR 真机测试后，再决定单摄与其他平台计划。
