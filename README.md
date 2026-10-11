# Project Alice

## 当前优先：Alice·空间伙伴 0.2.3（iPhone LiDAR）

现在优先开发 `AliceVision.iOS`：原生 ARKit 后置相机预览、水平面、LiDAR 场景网格与深度、
真实物体遮挡、角色导入与骨骼动作、视觉 API、自主活动区与禁区。目标测试设备为 iPhone 15 Pro。
**Android 和单摄算法暂缓；iOS 版不调用下面的边缘估算算法。**

打开 `ProjectAlice.iOS.sln` 查看 iPhone 项目。GitHub Actions 使用 macOS + Xcode 构建实际
设备 ARM64 Release IPA，通过包检查后发布 GitHub Release。IPA 未签名，安装前需要你重新签名。

新图标和主屏名称已加入原生安装包。内置原创角色“小光”可直接测试挥手、待机和迈步；
也可从系统“文件”导入 USDZ / DAE / OBJ / GLB / VRM，自动调整到默认 35 cm、脚底贴合平面。
单文件导入上限 1 GB（1024 MB），但仍受几何、纹理和手机内存保护限制，不保证任意 1 GB 模型可用。
GLB / VRM 贴图保留原始分辨率、不自动缩小或重压缩；按需逐张读取，加载前和加载中检查应用内存余量。
4K 多贴图不再因旧的总像素阈值直接拒绝。确实超出设备内存时会明确停止，系统内存警告会卸载大模型，而不是偷偷降画质。
GLB / VRM 保留皮肤与骨骼绑定，具有标准 VRM 人形映射的模型可做程序化招手、欢迎、点头、鞠躬、舞动和迈步。
0.2.3 修复原生矩阵行/列转换导致的拉长网格与蒙皮错位，统一绑定空间；贴图坐标保留原始 UV，不再额外翻转 V。
复杂模型的单个蒙皮绑定现在支持最多 1024 根骨骼（原上限 256）；保留 UInt16 索引、原始网格与有效权重。
GLB/VRM 几何安全上限提升为 200 万顶点、400 万三角形；读取网格数组前先估算内存，逐步检查原画质贴图、骨骼和 GPU 开销，不自动减面。
结构超限或字段类型错误会显示具体字段、实际数量与上限，便于定位，不再只提示“模型数组大小或类型无效”。
没有可识别人形骨骼的模型仍可显示；动作根据实际能力开放。不包含 GLB 原生动画片段、表情、弹簧物理或完整 MToon 还原。
USDZ / DAE 的自带命名动画可手动播放。其他格式和着色器不保证等价还原。

主界面提供“导入角色 / 角色动作 / 更多 / 启动 AI”和常驻急停。扫描后首次点击放置角色，再点同一平面选目标，
在动作菜单确认移动；也可先画绿色活动区与红色禁区，让 AI 自主选择动作。移动限制为已扫描的同一水平面、每次最多 1.5 米，并保守检查扫描网格；
这不是完整寻路，也不是可用于现实安全的避障系统。没有行走动画的模型只进行空间平移。

默认画面仅在本机处理。明确同意并启动 AI 后，才每隔 15 秒（可设 10–120 秒）上传一张最长边 768 像素的当前 AR 画面到你配置的 HTTPS 视觉接口。
支持自定义兼容接口、OpenRouter、Google Gemini 兼容接口，可主动获取模型列表或手填 ID。Key 仅存 iOS 钥匙串，不写入 Git、普通配置或日志。
AI 只选择程序提供的动作和已检查目标 ID；不能执行任意坐标、代码或真实设备控制。急停、后台、区域/模型变化、追踪不稳会暂停，必须手动重新开启。
第三方服务可能收费或保存图片，免费额度与视觉能力由服务商决定。详见 [AI 与活动区域](docs/ai-companion.md)。
第三方角色由用户个人下载后本机导入，受限的阿罗娜模型不在仓库或公开 IPA 中。
详见 [模型导入与阿罗娜来源](docs/character-models.md)。
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
dotnet run --project .\AliceVision.ModelTests\AliceVision.ModelTests.csproj --configuration Release
dotnet run --project .\AliceVision.AgentTests\AliceVision.AgentTests.csproj --configuration Release
dotnet build .\ProjectAlice.sln --configuration Debug
```

生成的调试 APK 位于：

```text
AliceVision.Android\bin\Debug\net10.0-android\com.projectalice.vision-Signed.apk
```

Android 原型保留，但当前不扩展。等 iPhone LiDAR 真机测试后，再决定单摄与其他平台计划。
