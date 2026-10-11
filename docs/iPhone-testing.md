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
- 0.2.3 请重新导入之前出现拉长三角形的 VRM，检查待机及招手时网格不飞散，眼睛/面部/衣服的图集区域与原模型一致。
- 0.2.4 检查发光装饰的颜色与纹理；若模型材质元数据已另存修正副本，请导入副本，检查透明眼睛和高光不再遮住瞳孔、衣服使用颜色图而不是阴影遮罩。
- 0.2.6 请重点录屏左右招手：从身体外侧抬肘，手保持在脸的外侧，掌心朝前，第一次向外摆，主要左右摆手腕而不是上下摆整条手臂，结束时平滑放下。
- 欢迎应在胸腰高度张开手，不再是脸旁双手投降；手指不应横向扭曲。中途打开普通菜单应收手回待机，红色急停则应立即冻结，不自动继续收手。
- 先招手、再点头/待机，以及招手中途急停：观察是否突然跳姿、肘关节反折或左右手不对称。待机应是微弱呼吸/重心变化，不是同步左右摇头。
- 左右招手、欢迎、点头、鞠躬、轻轻晃动；连续切动作应平滑，不重置到 T-pose。急停后姿态会冻结，再明确选择“待机”才平滑恢复。
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
dotnet run --project AliceVision.MotionTests/AliceVision.MotionTests.csproj --configuration Release
dotnet run --project AliceVision.ModelTests/AliceVision.ModelTests.csproj -- --inspect-model "你的模型.vrm"
pwsh -File scripts/check-ios-transforms.ps1
```

Windows 的模型解析/测试成功不等于手机材质渲染成功。iPhone 原生包由 GitHub Actions 构建。
动作测试覆盖曲线端点、缓入缓出、左右镜像、不同初始手臂姿态和两骨长度约束；只能验证数学与时序，不保证每个模型看起来像真人。
本地骨骼预览直接使用生产动作求解、模型的实际人形绑定元数据及父子局部姿态过渡；检查前视、侧视、掌心和欢迎切换招手。预览不加载贴图网格，不验证指骨蒙皮、道具抓握、布料、表情和手机上的最终观感。
安装 iOS workload 后，矩阵脚本会编译生产转换代码并执行实际 iOS 实现程序集的纯托管矩阵布局、四元数桥接与父子旋转组合检查；
不创建 UIKit 对象或调用原生设备接口，不能替代 iPhone 图像渲染测试。
`ProjectAlice.sln` 保留旧 Windows / Android 工程；iPhone 使用 `ProjectAlice.iOS.sln`。
不修改 `lingdongdao` 或其他无关工程。当前尚不能在本机验证用户报告的 iOS 27。
